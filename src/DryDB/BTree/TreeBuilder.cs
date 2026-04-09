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

static class TreeBuilder
{
    static readonly int PageHeaderSize = Unsafe.SizeOf<PageHeader>() + Unsafe.SizeOf<NodeHeader>();
    static readonly int RightSiblingPositionPageOffset = PageHeaderSize - sizeof(long);

    public static async ValueTask<TreeBuildResult> BuildToAsync(
        Stream outStream,
        int pageSize,
        KeyValueList keyValues,
        IReadOnlyList<IPageFilter>? pageFilters = null,
        CancellationToken cancellationToken = default)
    {
        if (pageSize < PageHeaderSize + 32)
        {
            throw new ArgumentException("pageSize too small");
        }

        var wroteValuePointers = new List<PageRef>(keyValues.Count);

        var nodes = new List<NodeEntry> { new(pageSize) };
        nodes[0].Reset();

        var leaf = nodes[0];
        foreach (var (key, value) in keyValues)
        {
            if (leaf.EntryCount <= 0)
            {
                leaf.FirstKey = key;
            }

            var isOverflow = false;
            var inlineNeeds = PageHeaderSize + (leaf.EntryCount + 1) * (sizeof(int) + sizeof(ushort) * 2) +
                        leaf.KeyValueBufferOffset + key.Length + value.Length;
            if (inlineNeeds > pageSize)
            {
                // Check if it fits as overflow (key + 8-byte PageNumber.Value)
                var overflowNeeds = PageHeaderSize + (leaf.EntryCount + 1) * (sizeof(int) + sizeof(ushort) * 2) +
                            leaf.KeyValueBufferOffset + key.Length + sizeof(long);
                if (overflowNeeds > pageSize)
                {
                    // Current page is full even for overflow; rotate
                    await RotatePageAsync(outStream, nodes, 0, true, wroteValuePointers, pageFilters, cancellationToken)
                        .ConfigureAwait(false);
                    if (nodes[0].EntryCount <= 0)
                    {
                        nodes[0].FirstKey = key;
                    }
                    leaf = nodes[0];
                }

                // Recalculate after potential rotation
                inlineNeeds = PageHeaderSize + (leaf.EntryCount + 1) * (sizeof(int) + sizeof(ushort) * 2) +
                            leaf.KeyValueBufferOffset + key.Length + value.Length;
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

            // Copy key into buffer
            Unsafe.CopyBlockUnaligned(
                ref Unsafe.Add(ref GetArrayDataReference(leaf.KeyValueBuffer), leaf.KeyValueBufferOffset),
                ref MemoryMarshal.GetReference(key.Span),
                (uint)key.Length);

            if (isOverflow)
            {
                // Write blob page and store PageNumber.Value as inline payload
                var blobPageNumber = await WriteBlobPageAsync(outStream, value, pageFilters, cancellationToken)
                    .ConfigureAwait(false);

                Unsafe.WriteUnaligned(
                    ref Unsafe.Add(ref GetArrayDataReference(leaf.KeyValueBuffer), leaf.KeyValueBufferOffset + key.Length),
                    blobPageNumber.Value);

                leaf.KeyValueSizes.Add((key.Length, sizeof(long)));
                leaf.OverflowPageRefs.Add(new PageRef(blobPageNumber, PageHeaderSize, value.Length));
                leaf.KeyValueBufferOffset += key.Length + sizeof(long);
            }
            else
            {
                // Inline value
                Unsafe.CopyBlockUnaligned(
                    ref Unsafe.Add(ref GetArrayDataReference(leaf.KeyValueBuffer), leaf.KeyValueBufferOffset + key.Length),
                    ref MemoryMarshal.GetReference(value.Span),
                    (uint)value.Length);

                leaf.KeyValueSizes.Add((key.Length, value.Length));
                leaf.OverflowPageRefs.Add(null);
                leaf.KeyValueBufferOffset += key.Length + value.Length;
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
                    await RotatePageAsync(outStream, nodes, level, true, wroteValuePointers, pageFilters, cancellationToken).ConfigureAwait(false);
                    anyFlushed = true;
                }
            }
            if (!anyFlushed) break;
        }

        // Write the root (top level)
        if (nodes[^1].EntryCount > 0)
        {
            await RotatePageAsync(outStream, nodes, nodes.Count - 1, false, wroteValuePointers, pageFilters, cancellationToken).ConfigureAwait(false);
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
        ReadOnlyMemory<byte> value,
        IReadOnlyList<IPageFilter>? filters,
        CancellationToken cancellationToken)
    {
        var blobPageNumber = new PageNumber(outStream.Position);
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
        List<NodeEntry> nodeEntries,
        int level,
        bool promote,
        List<PageRef> wroteValueRefs,
        IReadOnlyList<IPageFilter>? pageFilters = null,
        CancellationToken cancellationToken = default)
    {
        var currentNode = nodeEntries[level];
        var currentPos = new PageNumber(outStream.Position);

        var kind = level == 0 ? NodeKind.Leaf : NodeKind.Internal;
        var nodeHeader = new NodeHeader
        {
            Kind = kind,
            EntryCount = currentNode.EntryCount,
            LeftSiblingPageNumber = currentNode.PrevNodeStartPageNumber,
            RightSiblingPageNumber = PageNumber.Empty
        };

        await FlushPageAsync(outStream, nodeHeader, currentNode, wroteValueRefs, pageFilters, cancellationToken)
            .ConfigureAwait(false);

        // Patch RightSiblingPosition
        if (!currentNode.PrevNodeStartPageNumber.IsEmpty)
        {
            var saved = outStream.Position;
            outStream.Seek(currentNode.PrevNodeStartPageNumber.Value + RightSiblingPositionPageOffset, SeekOrigin.Begin);
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

        var needs = PageHeaderSize + (parent.EntryCount + 1) * (sizeof(int) + sizeof(ushort)) +
                    parent.KeyValueBufferOffset + sepKey.Length + sizeof(long);
        if (needs > parent.PageSize)
        {
            await RotatePageAsync(outStream, nodeEntries, parentLevel, true, wroteValueRefs, pageFilters, cancellationToken)
                .ConfigureAwait(false);
            if (parent.EntryCount == 0) parent.FirstKey = sepKey; // first key of new page
        }

        ref var parentKeyValueBufferReference = ref Unsafe.Add(
                ref GetArrayDataReference(parent.KeyValueBuffer),
            parent.KeyValueBufferOffset);

        Unsafe.CopyBlockUnaligned(
            ref parentKeyValueBufferReference,
            ref MemoryMarshal.GetReference(sepKey.Span),
            (uint)sepKey.Length);
        parentKeyValueBufferReference = ref Unsafe.Add(ref parentKeyValueBufferReference, sepKey.Length);

        Unsafe.WriteUnaligned(ref parentKeyValueBufferReference, currentPos.Value);

        parent.KeyValueSizes.Add((sepKey.Length, sizeof(long)));
        parent.OverflowPageRefs.Add(null); // internal nodes never overflow
        parent.KeyValueBufferOffset += sepKey.Length + sizeof(long);

        currentNode.Reset();
        currentNode.PrevNodeStartPageNumber = currentPos;
    }

    static async ValueTask FlushPageAsync(
        Stream outStream,
        NodeHeader nodeHeader,
        NodeEntry node,
        List<PageRef> wroteValueRefs,
        IReadOnlyList<IPageFilter>? filters,
        CancellationToken cancellationToken = default)
    {
        var currentPageNumber = new PageNumber(outStream.Position);

        var pageLength = PageHeaderSize +
                         node.EntryCount * (nodeHeader.Kind == NodeKind.Leaf
                             ? sizeof(int) + sizeof(ushort) * 2
                             : sizeof(int) + sizeof(ushort)) +
                         node.KeyValueBufferOffset;

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

        // write meta(s)
        var metaSize = nodeHeader.Kind == NodeKind.Leaf
            ? sizeof(int) + sizeof(ushort) * 2
            : sizeof(int) + sizeof(ushort);

        var payloadOffset = PageHeaderSize + node.EntryCount * metaSize;

        for (var i = 0; i < node.KeyValueSizes.Count; i++)
        {
            var (keyLength, valueLength) = node.KeyValueSizes[i];

            if (nodeHeader.Kind == NodeKind.Leaf)
            {
                var overflowRef = node.OverflowPageRefs[i];
                if (overflowRef.HasValue)
                {
                    wroteValueRefs.Add(overflowRef.Value);
                }
                else
                {
                    wroteValueRefs.Add(new PageRef(currentPageNumber, payloadOffset + keyLength, valueLength));
                }
            }

            Unsafe.WriteUnaligned(ref ptr, payloadOffset);
            ptr = ref Unsafe.Add(ref ptr, sizeof(int));

            Unsafe.WriteUnaligned(ref ptr, (ushort)keyLength);
            ptr = ref Unsafe.Add(ref ptr, sizeof(ushort));

            if (nodeHeader.Kind == NodeKind.Leaf)
            {
                var overflowRef = node.OverflowPageRefs[i];
                // variable length value (sentinel for overflow)
                Unsafe.WriteUnaligned(ref ptr,
                    overflowRef.HasValue ? LeafNodeReader.OverflowSentinel : (ushort)valueLength);
                ptr = ref Unsafe.Add(ref ptr, sizeof(ushort));
            }
            payloadOffset += keyLength + valueLength;
        }

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
