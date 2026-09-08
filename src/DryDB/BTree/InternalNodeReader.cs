using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using DryDB.Internal;

namespace DryDB.BTree;

/// <summary>
///  Internal Node Reader
/// </summary>
/// <remarks>
/// Supports both meta layouts (see <see cref="NodeFlags"/>): the classic 6-byte record
/// per entry, and the compact layout (format 1.4) where the meta area is
/// <c>ushort offsets[entryCount + 1]</c> — the payload per entry is key + 8-byte child
/// ordinal, so the key length falls out of adjacent offsets. Pages flagged
/// <see cref="NodeFlags.OmittedKeys"/> have no meta area at all: the payload is a
/// dense array of 8-byte child ordinals and separator comparisons reduce to the exact
/// digests.
/// </remarks>
readonly ref struct InternalNodeReader
{
    [StructLayout(LayoutKind.Explicit, Size = 6, Pack = 1)]
    struct NodeEntryMeta
    {
        [FieldOffset(0)]
        public int PageOffset;

        [FieldOffset(4)]
        public ushort KeyLength;
    }

    static readonly int DigestBase = Unsafe.SizeOf<PageHeader>() + Unsafe.SizeOf<NodeHeader>();

#if NETSTANDARD
    readonly ReadOnlySpan<byte> page;
#else
    readonly ref byte pageReference;
#endif
    readonly int entryCount;
    readonly int metaBase;
    readonly bool hasKeyDigests;
    readonly bool hasEytzingerDigests;
    readonly bool compactMeta;
    readonly bool omittedKeys;

    public InternalNodeReader(ReadOnlySpan<byte> page, in NodeHeader header)
    {
#if NETSTANDARD
        this.page = page;
#else
        pageReference = ref MemoryMarshal.GetReference(page);
#endif
        entryCount = header.EntryCount;
        hasKeyDigests = header.HasKeyDigests;
        hasEytzingerDigests = header.HasEytzingerDigests;
        compactMeta = header.HasCompactMeta;
        omittedKeys = header.HasOmittedKeys;
        metaBase = DigestBase + (hasKeyDigests
            ? (hasEytzingerDigests ? EytzingerLayout.CompleteSize(entryCount) : entryCount) * sizeof(ulong)
            : 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void GetAt(int index, out ReadOnlySpan<byte> key, out PageNumber childPageNumber)
    {
#if NETSTANDARD
        ref var pageReference = ref MemoryMarshal.GetReference(page);
#endif
        var meta = GetMeta(index);
        ref var ptr = ref Unsafe.Add(ref pageReference, meta.PageOffset);

        key = MemoryMarshal.CreateReadOnlySpan(ref ptr, meta.KeyLength);
        ptr = ref Unsafe.Add(ref ptr, meta.KeyLength);

        childPageNumber = Unsafe.ReadUnaligned<PageNumber>(ref ptr);
    }

    public bool TrySearch<TComparer>(
        ReadOnlySpan<byte> key,
        TComparer comparer,
        ulong keyDigest,
        bool hasKeyDigest,
        out PageNumber childPageNumber)
        where TComparer : struct, IKeyComparer
    {
#if NETSTANDARD
        ref var pageReference = ref MemoryMarshal.GetReference(page);
#endif
        if (omittedKeys && !hasKeyDigest) ThrowOmittedKeysNeedDigest();

        NodeEntryMeta meta;
        if (hasEytzingerDigests && hasKeyDigest)
        {
            // Branch-free descent to the rank of the first entry with digest >=
            // keyDigest, then advance through the run of equal digests with full
            // comparisons to reach the upper bound (first entry > key).
            var i = EytzingerLayout.LowerBoundRank(
                ref pageReference, DigestBase, (metaBase - DigestBase) / sizeof(ulong), keyDigest);
            while (i < entryCount && CompareEntry(ref pageReference, i, key, keyDigest, comparer) <= 0)
            {
                i++;
            }

            var childIndex = i == 0 ? 0 : i - 1;
            meta = GetMeta(childIndex);
            childPageNumber = Unsafe.ReadUnaligned<PageNumber>(
                ref Unsafe.Add(
                    ref pageReference,
                    meta.PageOffset + meta.KeyLength));
            return true;
        }

        // Reaching here with hasEytzingerDigests set means hasKeyDigest is false, so
        // useDigest stays false and neither digest path below touches the (Eytzinger-
        // ordered) digest array.
        var useDigest = hasKeyDigests && hasKeyDigest;
        int min;
        if (useDigest && DigestSearch.IsAccelerated)
        {
            // Branch-free lower bound over the digest array, then advance through the
            // run of equal digests with full comparisons to reach the upper bound
            // (first entry > key). Entries with a greater digest are already > key.
            min = DigestSearch.LowerBound(ref pageReference, DigestBase, entryCount, keyDigest);
            while (min < entryCount)
            {
                var digest = Unsafe.ReadUnaligned<ulong>(
                    ref Unsafe.Add(ref pageReference, DigestBase + min * sizeof(ulong)));
                if (digest != keyDigest) break;
                if (CompareEntry(ref pageReference, min, key, keyDigest, comparer) > 0) break;
                min++;
            }
        }
        else
        {
            min = 0;
            var max = entryCount;
            while (min < max)
            {
                var mid = min + ((max - min) >> 1);

                int cmp;
                if (useDigest)
                {
                    // One contiguous load instead of dereferencing the variable-length
                    // key; only digest ties fall back to the full comparison.
                    var digest = Unsafe.ReadUnaligned<ulong>(
                        ref Unsafe.Add(ref pageReference, DigestBase + mid * sizeof(ulong)));
                    cmp = digest != keyDigest
                        ? (digest < keyDigest ? -1 : 1)
                        : CompareEntry(ref pageReference, mid, key, keyDigest, comparer);
                }
                else
                {
                    cmp = CompareEntry(ref pageReference, mid, key, keyDigest, comparer);
                }

                if (cmp <= 0) // upper bounds
                {
                    min = mid + 1;
                }
                else
                {
                    max = mid;
                }
            }
        }

        var index = min == 0 ? 0 : min - 1;
        meta = GetMeta(index);
        childPageNumber = Unsafe.ReadUnaligned<PageNumber>(
            ref Unsafe.Add(
                ref pageReference,
                meta.PageOffset + meta.KeyLength));
        return true;
    }

    /// <summary>
    /// Compares the index-th separator against the search key. On
    /// <see cref="NodeFlags.OmittedKeys"/> pages the digest is exact and no key bytes
    /// exist, so the comparison reduces to the digest order.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    int CompareEntry<TComparer>(ref byte pageReference, int index, ReadOnlySpan<byte> key, ulong keyDigest, TComparer comparer)
        where TComparer : struct, IKeyComparer
    {
        if (omittedKeys)
        {
            var digest = Unsafe.ReadUnaligned<ulong>(
                ref Unsafe.Add(ref pageReference, DigestBase + index * sizeof(ulong)));
            return (digest > keyDigest ? 1 : 0) - (digest < keyDigest ? 1 : 0);
        }

        var meta = GetMeta(index);
        var entryKey = MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.Add(ref pageReference, meta.PageOffset),
            meta.KeyLength);
        return comparer.Compare(entryKey, key);
    }

    static void ThrowOmittedKeysNeedDigest() =>
        throw new InvalidOperationException(
            "This page stores no key bytes (OmittedKeys); searching it requires a key digest.");

    // for debug purpose
    public KeyValuePair<Memory<byte>, long>[] ToArray()
    {
#if NETSTANDARD
        ref var pageReference= ref MemoryMarshal.GetReference(page);
#endif

        var list = new List<KeyValuePair<Memory<byte>, long>>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var meta = GetMeta(i);

            var key = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.Add(ref pageReference, meta.PageOffset),
                meta.KeyLength);

            var childPosition = Unsafe.ReadUnaligned<long>(
                ref Unsafe.Add(ref pageReference, meta.PageOffset + meta.KeyLength));

            list.Add(new KeyValuePair<Memory<byte>, long>(key.ToArray(), childPosition));
        }
        return list.ToArray();
    }

    public string Dump()
    {
        var a = ToArray();
        var b = new StringBuilder();
        foreach (var (k, v) in a)
        {
            b.AppendLine($"k={Encoding.UTF8.GetString(k.Span)},v={v}");
        }
        return b.ToString();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    NodeEntryMeta GetMeta(int index)
    {
#if NETSTANDARD
        ref var pageReference = ref MemoryMarshal.GetReference(page);
#endif
        if (!compactMeta)
        {
            ref var recordPtr = ref Unsafe.Add(
                ref pageReference,
                metaBase + index * Unsafe.SizeOf<NodeEntryMeta>());
            return Unsafe.ReadUnaligned<NodeEntryMeta>(ref recordPtr);
        }

        if (omittedKeys)
        {
            // No meta area: the payload is a dense array of 8-byte child ordinals
            // starting right after the digests.
            return new NodeEntryMeta
            {
                PageOffset = metaBase + index * sizeof(long),
                KeyLength = 0,
            };
        }

        // Compact layout: ushort offsets[entryCount + 1]; each entry's payload is
        // key + 8-byte child ordinal, so the key length is derived.
        var offset = Unsafe.ReadUnaligned<ushort>(
            ref Unsafe.Add(ref pageReference, metaBase + index * sizeof(ushort)));
        var nextOffset = Unsafe.ReadUnaligned<ushort>(
            ref Unsafe.Add(ref pageReference, metaBase + (index + 1) * sizeof(ushort)));

        return new NodeEntryMeta
        {
            PageOffset = offset,
            KeyLength = (ushort)(nextOffset - offset - sizeof(long)),
        };
    }
}
