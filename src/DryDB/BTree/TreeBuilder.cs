using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DryDB.Internal;
#if NET7_0_OR_GREATER
using static System.Runtime.InteropServices.MemoryMarshal;
#else
using static System.Runtime.CompilerServices.MemoryMarshalEx;
#endif

namespace DryDB.BTree;

sealed class NodeEntry(int pageSize)
{
    public readonly byte[] KeyValueBuffer = new byte[pageSize];
    public readonly List<(int KeyLength, int ValueLength)> KeyValueSizes = [];
    public readonly List<PageRef?> OverflowPageRefs = [];
    public readonly List<ulong> Digests = [];
    public int KeyValueBufferOffset;
    public PageNumber PrevNodeStartPageNumber = PageNumber.Empty;
    public ReadOnlyMemory<byte>? FirstKey;

    public int PageSize => pageSize;
    public int EntryCount => KeyValueSizes.Count;

    public void Reset()
    {
        Array.Clear(KeyValueBuffer, 0, KeyValueBuffer.Length);
        KeyValueSizes.Clear();
        OverflowPageRefs.Clear();
        Digests.Clear();
        KeyValueBufferOffset = 0;
        PrevNodeStartPageNumber = PageNumber.Empty;
        FirstKey = null;
    }
}

sealed class TreeBuildResult
{
    public PageNumber RootPageNumber { get; init; }
    public List<PageRef> WroteValueRefs { get; init; }
}

/// <summary>
/// Per-tree layout decisions and the size arithmetic they imply. See
/// <see cref="NodeFlags"/> for the on-disk shapes.
/// </summary>
readonly struct NodeLayout(IKeyEncoding digestEncoding, bool eytzinger, bool compactMeta, bool omitKeys)
{
    public IKeyEncoding DigestEncoding => digestEncoding;
    public bool Eytzinger => eytzinger;
    public bool CompactMeta => compactMeta;
    public bool OmitKeys => omitKeys;

    public int Flags =>
        (eytzinger ? NodeFlags.EytzingerDigests : 0)
        | (compactMeta ? NodeFlags.CompactMeta : 0)
        | (omitKeys ? NodeFlags.OmittedKeys : 0);

    /// <summary>
    /// Bytes the digest array occupies for a node of <paramref name="entryCount"/>
    /// entries: one slot per entry in sorted order, or a MaxValue-padded complete tree
    /// (2^k - 1 slots) in Eytzinger order.
    /// </summary>
    public int DigestAreaSize(int entryCount) =>
        (eytzinger ? EytzingerLayout.CompleteSize(entryCount) : entryCount) * sizeof(ulong);

    public int LeafMetaSize(int entryCount) => compactMeta
        ? (entryCount + 1 + (omitKeys ? 0 : entryCount)) * sizeof(ushort)
        : entryCount * (sizeof(int) + sizeof(ushort) * 2);

    public int InternalMetaSize(int entryCount) => compactMeta
        ? (omitKeys ? 0 : (entryCount + 1) * sizeof(ushort))
        : entryCount * (sizeof(int) + sizeof(ushort));

    /// <summary>Bytes the key contributes to the payload (zero when keys are omitted).</summary>
    public int StoredKeyLength(int keyLength) => omitKeys ? 0 : keyLength;
}

static class TreeBuilder
{
    static readonly int PageHeaderSize = Unsafe.SizeOf<PageHeader>() + Unsafe.SizeOf<NodeHeader>();
    static readonly int RightSiblingPositionPageOffset = PageHeaderSize - sizeof(long);

    /// <summary>
    /// Largest page size the compact meta layout supports: offsets are 15-bit (bit 15
    /// of a leaf offset flags an overflow entry), and the closing sentinel offset can
    /// equal the page length, so both must fit in 15 bits. Larger pages keep the
    /// classic meta records.
    /// </summary>
    internal const int MaxCompactPageSize = 0x7FFF;

    public static async ValueTask<TreeBuildResult> BuildToAsync(
        Stream outStream,
        int pageSize,
        KeyValueList keyValues,
        PageDirectory pageDirectory,
        IReadOnlyList<IPageFilter>? pageFilters = null,
        bool eytzingerDigests = false,
        CancellationToken cancellationToken = default)
    {
        if (pageSize < PageHeaderSize + 32)
        {
            throw new ArgumentException("pageSize too small");
        }

        var wroteValuePointers = new List<PageRef>(keyValues.Count);

        // Every node carries a contiguous 8-byte order-preserving digest per entry,
        // searched instead of the scattered keys (digests are mandatory as of 1.4).
        var digestEncoding = keyValues.KeyEncoding;
        var eytzinger = eytzingerDigests;
        var compactMeta = pageSize <= MaxCompactPageSize;
        // Exact digests are a bijective image of the keys: skip storing the key bytes
        // entirely (never with Eytzinger — enumeration needs sorted-order digests).
        var omitKeys = compactMeta && !eytzinger && digestEncoding.IsKeyDigestExact;
        var layout = new NodeLayout(digestEncoding, eytzinger, compactMeta, omitKeys);

        var nodes = new List<NodeEntry> { new(pageSize) };
        nodes[0].Reset();

        var leaf = nodes[0];
        foreach (var (key, value) in keyValues)
        {
            if (leaf.EntryCount <= 0)
            {
                leaf.FirstKey = key;
            }

            var storedKeyLength = layout.StoredKeyLength(key.Length);
            var isOverflow = false;
            var inlineNeeds = PageHeaderSize + layout.LeafMetaSize(leaf.EntryCount + 1) +
                        layout.DigestAreaSize(leaf.EntryCount + 1) +
                        leaf.KeyValueBufferOffset + storedKeyLength + value.Length;
            if (inlineNeeds > pageSize)
            {
                // Check if it fits as overflow (key + 8-byte PageNumber.Value)
                var overflowNeeds = PageHeaderSize + layout.LeafMetaSize(leaf.EntryCount + 1) +
                            layout.DigestAreaSize(leaf.EntryCount + 1) +
                            leaf.KeyValueBufferOffset + storedKeyLength + sizeof(long);
                if (overflowNeeds > pageSize)
                {
                    // Current page is full even for overflow; rotate
                    await RotatePageAsync(outStream, pageDirectory, nodes, 0, true, wroteValuePointers, layout, pageFilters, cancellationToken)
                        .ConfigureAwait(false);
                    if (nodes[0].EntryCount <= 0)
                    {
                        nodes[0].FirstKey = key;
                    }
                    leaf = nodes[0];
                }

                // Recalculate after potential rotation
                inlineNeeds = PageHeaderSize + layout.LeafMetaSize(leaf.EntryCount + 1) +
                            layout.DigestAreaSize(leaf.EntryCount + 1) +
                            leaf.KeyValueBufferOffset + storedKeyLength + value.Length;
                if (inlineNeeds > pageSize)
                {
                    isOverflow = true;
                }
            }

            // Value length that equals the overflow sentinel must be stored as overflow
            // to avoid ambiguity during read.
            if (!isOverflow && value.Length == LeafNodeReader.OverflowSentinel)
            {
                isOverflow = true;
            }

            leaf.Digests.Add(digestEncoding.GetKeyDigest(key.Span));

            // Copy key into buffer (skipped when the digest replaces the key)
            if (!layout.OmitKeys)
            {
                Unsafe.CopyBlockUnaligned(
                    ref Unsafe.Add(ref GetArrayDataReference(leaf.KeyValueBuffer), leaf.KeyValueBufferOffset),
                    ref MemoryMarshal.GetReference(key.Span),
                    (uint)key.Length);
            }

            if (isOverflow)
            {
                // Write blob page and store PageNumber.Value as inline payload
                var blobPageNumber = await WriteBlobPageAsync(outStream, pageDirectory, value, pageFilters, cancellationToken)
                    .ConfigureAwait(false);

                Unsafe.WriteUnaligned(
                    ref Unsafe.Add(ref GetArrayDataReference(leaf.KeyValueBuffer), leaf.KeyValueBufferOffset + storedKeyLength),
                    blobPageNumber.Value);

                leaf.KeyValueSizes.Add((storedKeyLength, sizeof(long)));
                leaf.OverflowPageRefs.Add(new PageRef(blobPageNumber, PageHeaderSize, value.Length));
                leaf.KeyValueBufferOffset += storedKeyLength + sizeof(long);
            }
            else
            {
                // Inline value
                Unsafe.CopyBlockUnaligned(
                    ref Unsafe.Add(ref GetArrayDataReference(leaf.KeyValueBuffer), leaf.KeyValueBufferOffset + storedKeyLength),
                    ref MemoryMarshal.GetReference(value.Span),
                    (uint)value.Length);

                leaf.KeyValueSizes.Add((storedKeyLength, value.Length));
                leaf.OverflowPageRefs.Add(null);
                leaf.KeyValueBufferOffset += storedKeyLength + value.Length;
            }
        }

        // Flush all non-top levels until no entries remain below the top.
        // Rotation cascades can create new levels, so repeat until stable.
        while (true)
        {
            var anyFlushed = false;
            for (var level = 0; level < nodes.Count - 1; level++)
            {
                if (nodes[level].EntryCount > 0)
                {
                    await RotatePageAsync(outStream, pageDirectory, nodes, level, true, wroteValuePointers, layout, pageFilters, cancellationToken).ConfigureAwait(false);
                    anyFlushed = true;
                }
            }
            if (!anyFlushed) break;
        }

        // Write the root (top level)
        if (nodes[^1].EntryCount > 0)
        {
            await RotatePageAsync(outStream, pageDirectory, nodes, nodes.Count - 1, false, wroteValuePointers, layout, pageFilters, cancellationToken).ConfigureAwait(false);
        }

        var rootPageNumber = nodes[^1].PrevNodeStartPageNumber; // latest root
        return new TreeBuildResult
        {
            RootPageNumber = rootPageNumber,
            WroteValueRefs = wroteValuePointers
        };
    }

    static async ValueTask<PageNumber> WriteBlobPageAsync(
        Stream outStream,
        PageDirectory pageDirectory,
        ReadOnlyMemory<byte> value,
        IReadOnlyList<IPageFilter>? filters,
        CancellationToken cancellationToken)
    {
        var blobPageNumber = pageDirectory.Add(outStream.Position);
        var pageLength = PageHeaderSize + value.Length;

        var buffer = ArrayPool<byte>.Shared.Rent(pageLength);
        ref var ptr = ref GetArrayDataReference(buffer);

        // write page header
        Unsafe.WriteUnaligned(ref ptr, new PageHeader { PageSize = pageLength });
        ptr = ref Unsafe.Add(ref ptr, Unsafe.SizeOf<PageHeader>());

        // write node header (Leaf, EntryCount=0)
        Unsafe.WriteUnaligned(ref ptr, new NodeHeader
        {
            Kind = NodeKind.Leaf,
            EntryCount = 0,
            LeftSiblingPageNumber = PageNumber.Empty,
            RightSiblingPageNumber = PageNumber.Empty
        });
        ptr = ref Unsafe.Add(ref ptr, Unsafe.SizeOf<NodeHeader>());

        // write value data
        Unsafe.CopyBlockUnaligned(
            ref ptr,
            ref MemoryMarshal.GetReference(value.Span),
            (uint)value.Length);

        await WritePageWithFiltersAsync(outStream, buffer, pageLength, filters, cancellationToken)
            .ConfigureAwait(false);

        ArrayPool<byte>.Shared.Return(buffer);
        return blobPageNumber;
    }

    static async ValueTask RotatePageAsync(
        Stream outStream,
        PageDirectory pageDirectory,
        List<NodeEntry> nodeEntries,
        int level,
        bool promote,
        List<PageRef> wroteValueRefs,
        NodeLayout layout,
        IReadOnlyList<IPageFilter>? pageFilters = null,
        CancellationToken cancellationToken = default)
    {
        var currentNode = nodeEntries[level];
        var currentPos = pageDirectory.Add(outStream.Position);

        var kind = level == 0 ? NodeKind.Leaf : NodeKind.Internal;
        var nodeHeader = new NodeHeader
        {
            Kind = (NodeKind)((int)kind | layout.Flags),
            EntryCount = currentNode.EntryCount,
            LeftSiblingPageNumber = currentNode.PrevNodeStartPageNumber,
            RightSiblingPageNumber = PageNumber.Empty
        };

        await FlushPageAsync(outStream, currentPos, nodeHeader, currentNode, wroteValueRefs, layout, pageFilters, cancellationToken)
            .ConfigureAwait(false);

        // Patch RightSiblingPosition
        if (!currentNode.PrevNodeStartPageNumber.IsEmpty)
        {
            var saved = outStream.Position;
            // PrevNodeStartPageNumber is an ordinal; resolve its file offset through the
            // directory to find the patch target. The value written is the ordinal.
            outStream.Seek(
                pageDirectory.Offsets[(int)currentNode.PrevNodeStartPageNumber.Value] + RightSiblingPositionPageOffset,
                SeekOrigin.Begin);
            var buffer = ArrayPool<byte>.Shared.Rent(sizeof(long));
            try
            {
                BinaryPrimitives.WriteInt64LittleEndian(buffer, currentPos.Value);
                await outStream.WriteAsync(buffer.AsMemory(0, sizeof(long)), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            await outStream.FlushAsync(cancellationToken);
            outStream.Seek(saved, SeekOrigin.Begin);
        }

        if (!promote)
        {
            currentNode.Reset();
            currentNode.PrevNodeStartPageNumber = currentPos;
            return;
        }

        var sepKey = currentNode.FirstKey!.Value;
        var parentLevel = level + 1;

        if (parentLevel >= nodeEntries.Count)
        {
            nodeEntries.Add(new NodeEntry(nodeEntries[0].PageSize));
            nodeEntries[^1].Reset();
        }

        var parent = nodeEntries[parentLevel];
        if (parent.EntryCount <= 0)
        {
            parent.FirstKey = sepKey;
        }

        var storedSepKeyLength = layout.StoredKeyLength(sepKey.Length);
        var needs = PageHeaderSize + layout.InternalMetaSize(parent.EntryCount + 1) +
                    layout.DigestAreaSize(parent.EntryCount + 1) +
                    parent.KeyValueBufferOffset + storedSepKeyLength + sizeof(long);
        if (needs > parent.PageSize)
        {
            await RotatePageAsync(outStream, pageDirectory, nodeEntries, parentLevel, true, wroteValueRefs, layout, pageFilters, cancellationToken)
                .ConfigureAwait(false);
            if (parent.EntryCount == 0) parent.FirstKey = sepKey; // first key of new page
        }

        parent.Digests.Add(layout.DigestEncoding.GetKeyDigest(sepKey.Span));

        ref var parentKeyValueBufferReference = ref Unsafe.Add(
                ref GetArrayDataReference(parent.KeyValueBuffer),
            parent.KeyValueBufferOffset);

        if (!layout.OmitKeys)
        {
            Unsafe.CopyBlockUnaligned(
                ref parentKeyValueBufferReference,
                ref MemoryMarshal.GetReference(sepKey.Span),
                (uint)sepKey.Length);
            parentKeyValueBufferReference = ref Unsafe.Add(ref parentKeyValueBufferReference, sepKey.Length);
        }

        Unsafe.WriteUnaligned(ref parentKeyValueBufferReference, currentPos.Value);

        parent.KeyValueSizes.Add((storedSepKeyLength, sizeof(long)));
        parent.OverflowPageRefs.Add(null); // internal nodes never overflow
        parent.KeyValueBufferOffset += storedSepKeyLength + sizeof(long);

        currentNode.Reset();
        currentNode.PrevNodeStartPageNumber = currentPos;
    }

    static async ValueTask FlushPageAsync(
        Stream outStream,
        PageNumber currentPageNumber,
        NodeHeader nodeHeader,
        NodeEntry node,
        List<PageRef> wroteValueRefs,
        NodeLayout layout,
        IReadOnlyList<IPageFilter>? filters,
        CancellationToken cancellationToken = default)
    {
        var isLeaf = nodeHeader.NodeKind == NodeKind.Leaf;
        var digestArea = layout.DigestAreaSize(node.EntryCount);
        var metaArea = isLeaf ? layout.LeafMetaSize(node.EntryCount) : layout.InternalMetaSize(node.EntryCount);
        var pageLength = PageHeaderSize + digestArea + metaArea + node.KeyValueBufferOffset;

        var buffer = ArrayPool<byte>.Shared.Rent(pageLength);
        ref var ptr = ref GetArrayDataReference(buffer);

        // write page header
        Unsafe.WriteUnaligned(ref ptr, new PageHeader
        {
            PageSize = pageLength
        });
        ptr = ref Unsafe.Add(ref ptr, Unsafe.SizeOf<PageHeader>());

        // write node header
        Unsafe.WriteUnaligned(ref ptr, nodeHeader);
        ptr = ref Unsafe.Add(ref ptr, Unsafe.SizeOf<NodeHeader>());

        // write key digests (computed when the entries were appended)
        {
            var digests = ArrayPool<ulong>.Shared.Rent(node.EntryCount);
            node.Digests.CopyTo(digests);

            if (layout.Eytzinger)
            {
                // Scatter the sorted digests into a MaxValue-padded complete tree in
                // Eytzinger order (see EytzingerLayout).
                var slotCount = digestArea / sizeof(ulong);
                var slots = ArrayPool<ulong>.Shared.Rent(slotCount);
                EytzingerLayout.Scatter(digests.AsSpan(0, node.EntryCount), slots.AsSpan(0, slotCount));
                for (var i = 0; i < slotCount; i++)
                {
                    Unsafe.WriteUnaligned(ref ptr, slots[i]);
                    ptr = ref Unsafe.Add(ref ptr, sizeof(ulong));
                }
                ArrayPool<ulong>.Shared.Return(slots);
            }
            else
            {
                for (var i = 0; i < node.EntryCount; i++)
                {
                    Unsafe.WriteUnaligned(ref ptr, digests[i]);
                    ptr = ref Unsafe.Add(ref ptr, sizeof(ulong));
                }
            }
            ArrayPool<ulong>.Shared.Return(digests);
        }

        var payloadBase = PageHeaderSize + digestArea + metaArea;

        // Record where every inline value lands (secondary index PageRefs point
        // straight at the value bytes).
        if (isLeaf)
        {
            var payloadOffset = payloadBase;
            for (var i = 0; i < node.KeyValueSizes.Count; i++)
            {
                var (keyLength, valueLength) = node.KeyValueSizes[i];
                var overflowRef = node.OverflowPageRefs[i];
                if (overflowRef.HasValue)
                {
                    wroteValueRefs.Add(overflowRef.Value);
                }
                else
                {
                    wroteValueRefs.Add(new PageRef(currentPageNumber, payloadOffset + keyLength, valueLength));
                }
                payloadOffset += keyLength + valueLength;
            }
        }

        // write meta(s)
        if (!layout.CompactMeta)
        {
            var payloadOffset = payloadBase;
            for (var i = 0; i < node.KeyValueSizes.Count; i++)
            {
                var (keyLength, valueLength) = node.KeyValueSizes[i];

                Unsafe.WriteUnaligned(ref ptr, payloadOffset);
                ptr = ref Unsafe.Add(ref ptr, sizeof(int));

                Unsafe.WriteUnaligned(ref ptr, (ushort)keyLength);
                ptr = ref Unsafe.Add(ref ptr, sizeof(ushort));

                if (isLeaf)
                {
                    // variable length value (sentinel for overflow)
                    Unsafe.WriteUnaligned(ref ptr,
                        node.OverflowPageRefs[i].HasValue ? LeafNodeReader.OverflowSentinel : (ushort)valueLength);
                    ptr = ref Unsafe.Add(ref ptr, sizeof(ushort));
                }
                payloadOffset += keyLength + valueLength;
            }
        }
        else if (isLeaf || !layout.OmitKeys)
        {
            // Compact: ushort offsets[entryCount + 1]. Leaf offsets flag overflow
            // entries in bit 15; the final slot closes the last entry's payload.
            var payloadOffset = payloadBase;
            for (var i = 0; i < node.KeyValueSizes.Count; i++)
            {
                var (keyLength, valueLength) = node.KeyValueSizes[i];
                var offset = (ushort)payloadOffset;
                if (isLeaf && node.OverflowPageRefs[i].HasValue)
                {
                    offset |= LeafNodeReader.CompactOverflowBit;
                }
                Unsafe.WriteUnaligned(ref ptr, offset);
                ptr = ref Unsafe.Add(ref ptr, sizeof(ushort));
                payloadOffset += keyLength + valueLength;
            }
            Unsafe.WriteUnaligned(ref ptr, (ushort)payloadOffset);
            ptr = ref Unsafe.Add(ref ptr, sizeof(ushort));

            if (isLeaf && !layout.OmitKeys)
            {
                for (var i = 0; i < node.KeyValueSizes.Count; i++)
                {
                    Unsafe.WriteUnaligned(ref ptr, (ushort)node.KeyValueSizes[i].KeyLength);
                    ptr = ref Unsafe.Add(ref ptr, sizeof(ushort));
                }
            }
        }
        // else: compact internal node with omitted keys has no meta area at all — the
        // payload is a dense array of 8-byte child ordinals.

        // write key/values
        ref var keyValuesReference = ref GetArrayDataReference(node.KeyValueBuffer);
        Unsafe.CopyBlockUnaligned(ref ptr, ref keyValuesReference, (uint)node.KeyValueBufferOffset);

        await WritePageWithFiltersAsync(outStream, buffer, pageLength, filters, cancellationToken)
            .ConfigureAwait(false);

        ArrayPool<byte>.Shared.Return(buffer);

        await outStream.FlushAsync(cancellationToken);

        // TODO: alignment
    }

    static async ValueTask WritePageWithFiltersAsync(
        Stream outStream,
        byte[] buffer,
        int pageLength,
        IReadOnlyList<IPageFilter>? filters,
        CancellationToken cancellationToken)
    {
        if (filters is { Count: > 0 })
        {
            var source = buffer.AsSpan(0, pageLength);
            var output = BufferWriterPool.Rent(pageLength);

            // copy header
            output.Write(source[..PageHeaderSize]);

            // encode
            filters[0].Encode(source[PageHeaderSize..], output);

            // update page header
            Unsafe.WriteUnaligned(
                ref MemoryMarshal.GetReference(output.WrittenSpan),
                new PageHeader { PageSize = output.WrittenCount });

            if (filters.Count <= 1)
            {
                await outStream.WriteAsync(output.WrittenMemory, cancellationToken);
            }
            else
            {
                // double buffer
                var input = output;
                output = BufferWriterPool.Rent(output.WrittenCount);

                for (var i = 1; i < filters.Count; i++)
                {
                    // copy header
                    output.Write(source[..PageHeaderSize]);

                    // encode
                    filters[i].Encode(input.WrittenSpan[PageHeaderSize..], output);

                    // update page header
                    Unsafe.WriteUnaligned(
                        ref MemoryMarshal.GetReference(output.WrittenSpan),
                        new PageHeader{ PageSize = output.WrittenCount });
                    (output, input) = (input, output);
                }

                await outStream.WriteAsync(output.WrittenMemory, cancellationToken);
                BufferWriterPool.Return(input);
            }
            BufferWriterPool.Return(output);
        }
        else
        {
            await outStream.WriteAsync(buffer.AsMemory(0, pageLength), cancellationToken);
        }
    }
}
