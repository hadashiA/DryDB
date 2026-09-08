using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using DryDB.Internal;

namespace DryDB.BTree;

/// <summary>
///  Leaf Node Reader
/// </summary>>
/// <remarks>
/// Supports both meta layouts: the classic 8-byte record per entry, and the compact
/// layout (format 1.4, <see cref="NodeFlags.CompactMeta"/>) where lengths are derived
/// from an absolute ushort offset array — see <see cref="NodeFlags"/> for the on-disk
/// shapes. Pages flagged <see cref="NodeFlags.OmittedKeys"/> carry no key bytes at
/// all: the digest array is exact, so every key comparison reduces to a digest
/// comparison.
/// </remarks>
readonly ref struct LeafNodeReader
{
    internal const ushort OverflowSentinel = ushort.MaxValue; // 0xFFFF

    /// <summary>Bit 15 of a compact leaf offset: the entry is an overflow value.</summary>
    internal const ushort CompactOverflowBit = 0x8000;
    internal const ushort CompactOffsetMask = 0x7FFF;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsOverflow(ushort valueLength) => valueLength == OverflowSentinel;

    [StructLayout(LayoutKind.Explicit, Size = 8, Pack = 1)]
    struct NodeEntryMeta
    {
        [FieldOffset(0)]
        public int PageOffset;

        [FieldOffset(4)]
        public ushort KeyLength;

        [FieldOffset(6)]
        public ushort ValueLength;
    }

    static readonly int DigestBase = Unsafe.SizeOf<PageHeader>() + Unsafe.SizeOf<NodeHeader>();

#if NETSTANDARD
    readonly ReadOnlySpan<byte> page;
#else
    readonly ref byte pageReference;
#endif
    readonly int entryCount;
    readonly int metaBase;
    readonly bool hasEytzingerDigests;
    readonly bool compactMeta;
    readonly bool omittedKeys;

    public LeafNodeReader(ReadOnlySpan<byte> page, in NodeHeader header)
    {
#if NETSTANDARD
        this.page = page;
#else
        pageReference = ref MemoryMarshal.GetReference(page);
#endif
        entryCount = header.EntryCount;
        hasEytzingerDigests = header.HasEytzingerDigests;
        compactMeta = header.HasCompactMeta;
        omittedKeys = header.HasOmittedKeys;
        // Every tree page carries a digest array (format 1.4: digests are mandatory).
        metaBase = DigestBase +
            (hasEytzingerDigests ? EytzingerLayout.CompleteSize(entryCount) : entryCount) * sizeof(ulong);
    }

    /// <summary>
    /// The digest of the index-th entry in sorted order. Only valid on pages whose
    /// digests are stored sorted (never Eytzinger); used to reconstruct keys on
    /// <see cref="NodeFlags.OmittedKeys"/> pages.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetDigestAt(int index)
    {
#if NETSTANDARD
        ref var pageReference = ref MemoryMarshal.GetReference(page);
#endif
        return Unsafe.ReadUnaligned<ulong>(
            ref Unsafe.Add(ref pageReference, DigestBase + index * sizeof(ulong)));
    }

    public void GetAt(int index, out ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value)
    {
#if NETSTANDARD
        ref var pageReference = ref MemoryMarshal.GetReference(page);
#endif
        var meta = GetMeta(index);
        // On OmittedKeys pages the key is not stored; KeyLength is 0 and the key span
        // comes back empty (reconstruct it from GetDigestAt via the encoding instead).
        key = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.Add(ref pageReference, meta.PageOffset),
            meta.KeyLength);

        value = MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.Add(ref pageReference, meta.PageOffset + meta.KeyLength),
            meta.ValueLength);
    }

    public void GetAt(int index, out int pageOffset, out ushort keyLength, out ushort valueLength)
    {
#if NETSTANDARD
        ref var pageReference = ref MemoryMarshal.GetReference(page);
#endif
        var meta = GetMeta(index);

        pageOffset = meta.PageOffset;
        keyLength = meta.KeyLength;
        valueLength = meta.ValueLength;
    }

    public bool TryFindValue<TComparer>(
        scoped ReadOnlySpan<byte> key,
        TComparer comparer,
        ulong keyDigest,
        out int index,
        out int valueOffset,
        out ushort valueLength)
        where TComparer : struct, IKeyComparer
    {
#if NETSTANDARD
        ref var pageReference = ref MemoryMarshal.GetReference(page);
#endif
        if (hasEytzingerDigests)
        {
            // Branch-free descent yields the rank of the first entry whose digest is
            // >= keyDigest; entries below the rank are < key. A match can only sit in
            // the run of equal digests starting there, resolved with full comparisons
            // (the digests are in Eytzinger order, so the run is walked via the keys).
            var i = EytzingerLayout.LowerBoundRank(
                ref pageReference, DigestBase, (metaBase - DigestBase) / sizeof(ulong), keyDigest);
            for (; i < entryCount; i++)
            {
                var compared = CompareEntry(ref pageReference, i, key, keyDigest, comparer);
                if (compared == 0)
                {
                    var meta = GetMeta(i);
                    index = i;
                    valueOffset = meta.PageOffset + meta.KeyLength;
                    valueLength = meta.ValueLength;
                    return true;
                }
                if (compared > 0) break;
            }

            index = default;
            valueOffset = default;
            valueLength = default;
            return false;
        }

        if (DigestSearch.IsAccelerated)
        {
            // Branch-free lower bound over the digest array; a match can only sit in
            // the run of digests equal to keyDigest, which starts at the bound.
            // (Order preservation: digest < keyDigest implies entry < key, and
            // digest > keyDigest implies entry > key.)
            var i = DigestSearch.LowerBound(ref pageReference, DigestBase, entryCount, keyDigest);
            for (; i < entryCount; i++)
            {
                var digest = Unsafe.ReadUnaligned<ulong>(
                    ref Unsafe.Add(ref pageReference, DigestBase + i * sizeof(ulong)));
                if (digest != keyDigest) break;

                var compared = CompareEntry(ref pageReference, i, key, keyDigest, comparer);
                if (compared == 0)
                {
                    var meta = GetMeta(i);
                    index = i;
                    valueOffset = meta.PageOffset + meta.KeyLength;
                    valueLength = meta.ValueLength;
                    return true;
                }
                if (compared > 0) break;
            }

            index = default;
            valueOffset = default;
            valueLength = default;
            return false;
        }

        var min = 0;
        var max = entryCount;

        while (min < max)
        {
            var midIndex = min + ((max - min) >> 1);

            // One contiguous load instead of dereferencing the variable-length key;
            // only digest ties fall back to the full comparison.
            var digest = Unsafe.ReadUnaligned<ulong>(
                ref Unsafe.Add(ref pageReference, DigestBase + midIndex * sizeof(ulong)));
            var compared = digest != keyDigest
                ? (digest < keyDigest ? -1 : 1)
                : CompareEntry(ref pageReference, midIndex, key, keyDigest, comparer);

            if (compared == 0)
            {
                var midMeta = GetMeta(midIndex);
                index = midIndex;
                valueOffset = midMeta.PageOffset + midMeta.KeyLength;
                valueLength = midMeta.ValueLength;
                return true;
            }
            if (compared < 0)
            {
                min = midIndex + 1;
            }
            else
            {
                max = midIndex;
            }
        }

        index = default;
        valueOffset = default;
        valueLength = default;
        return false;
    }

    public bool TrySearch<TComparer>(
        ReadOnlySpan<byte> key,
        SearchOperator op,
        TComparer comparer,
        ulong keyDigest,
        out int index)
        where TComparer : struct, IKeyComparer
    {
#if  NETSTANDARD
        ref var pageReference = ref MemoryMarshal.GetReference(page);
#endif
        if (hasEytzingerDigests)
        {
            // Branch-free descent to the rank of the first entry with digest >=
            // keyDigest, then resolve the bound inside the run of equal digests with
            // full comparisons. Entries past the run compare > key, which already
            // satisfies both bound operators and terminates the walk.
            var i = EytzingerLayout.LowerBoundRank(
                ref pageReference, DigestBase, (metaBase - DigestBase) / sizeof(ulong), keyDigest);
            switch (op)
            {
                case SearchOperator.Equal:
                    for (; i < entryCount; i++)
                    {
                        var compared = CompareEntry(ref pageReference, i, key, keyDigest, comparer);
                        if (compared == 0)
                        {
                            index = i;
                            return true;
                        }
                        if (compared > 0) break;
                    }
                    index = default;
                    return false;

                case SearchOperator.LowerBound:
                    // first entry >= key
                    while (i < entryCount && CompareEntry(ref pageReference, i, key, keyDigest, comparer) < 0)
                    {
                        i++;
                    }
                    break;

                case SearchOperator.UpperBound:
                    // first entry > key
                    while (i < entryCount && CompareEntry(ref pageReference, i, key, keyDigest, comparer) <= 0)
                    {
                        i++;
                    }
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(op), op, null);
            }

            if (i >= entryCount)
            {
                index = default;
                return false;
            }
            index = i;
            return true;
        }

        if (DigestSearch.IsAccelerated)
        {
            // Branch-free lower bound over the digest array, then resolve the bound
            // inside the run of equal digests with full comparisons (run length is
            // almost always 0 or 1; exact digests such as Int64 never exceed 1).
            // Entries before the bound are < key; the first entry with a greater
            // digest is > key, which already satisfies both bound operators.
            var i = DigestSearch.LowerBound(ref pageReference, DigestBase, entryCount, keyDigest);
            switch (op)
            {
                case SearchOperator.Equal:
                    for (; i < entryCount; i++)
                    {
                        var digest = Unsafe.ReadUnaligned<ulong>(
                            ref Unsafe.Add(ref pageReference, DigestBase + i * sizeof(ulong)));
                        if (digest != keyDigest) break;

                        var compared = CompareEntry(ref pageReference, i, key, keyDigest, comparer);
                        if (compared == 0)
                        {
                            index = i;
                            return true;
                        }
                        if (compared > 0) break;
                    }
                    index = default;
                    return false;

                case SearchOperator.LowerBound:
                    // first entry >= key
                    while (i < entryCount)
                    {
                        var digest = Unsafe.ReadUnaligned<ulong>(
                            ref Unsafe.Add(ref pageReference, DigestBase + i * sizeof(ulong)));
                        if (digest != keyDigest) break;
                        if (CompareEntry(ref pageReference, i, key, keyDigest, comparer) >= 0) break;
                        i++;
                    }
                    break;

                case SearchOperator.UpperBound:
                    // first entry > key
                    while (i < entryCount)
                    {
                        var digest = Unsafe.ReadUnaligned<ulong>(
                            ref Unsafe.Add(ref pageReference, DigestBase + i * sizeof(ulong)));
                        if (digest != keyDigest) break;
                        if (CompareEntry(ref pageReference, i, key, keyDigest, comparer) > 0) break;
                        i++;
                    }
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(op), op, null);
            }

            if (i >= entryCount)
            {
                index = default;
                return false;
            }
            index = i;
            return true;
        }

        var min = 0;
        var max = entryCount;
        var resultIndex = -1;

        while (min < max)
        {
            var midIndex = min + ((max - min) >> 1);

            var digest = Unsafe.ReadUnaligned<ulong>(
                ref Unsafe.Add(ref pageReference, DigestBase + midIndex * sizeof(ulong)));
            var compared = digest != keyDigest
                ? (digest < keyDigest ? -1 : 1)
                : CompareEntry(ref pageReference, midIndex, key, keyDigest, comparer);

            switch (op)
            {
                case SearchOperator.Equal:
                    if (compared == 0)
                    {
                        index = midIndex;
                        return true;
                    }
                    if (compared < 0)
                    {
                        min = midIndex + 1;
                        resultIndex = min;
                    }
                    else
                    {
                        max = midIndex;
                    }
                    break;
                case SearchOperator.LowerBound:
                    if (compared < 0)
                    {
                        min = midIndex + 1;
                    }
                    else
                    {
                        max = midIndex;
                        resultIndex = midIndex;
                    }
                    break;
                case SearchOperator.UpperBound:
                    if (compared <= 0)
                    {
                        min = midIndex + 1;
                    }
                    else
                    {
                        max = midIndex;
                        resultIndex = max;
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(op), op, null);
            }
        }

        if (resultIndex < 0)
        {
            index = default;
            return false;
        }

        switch (op)
        {
            case SearchOperator.Equal:
                if (min < max)
                {
                    if (CompareEntry(ref pageReference, min, key, keyDigest, comparer) == 0)
                    {
                        index = min;
                        return true;
                    }
                }
                index = default;
                return false;

            case SearchOperator.LowerBound:
                // >= key
                index = min;
                return true;

            case SearchOperator.UpperBound:
                // > key
                index = min;
                return true;

            default:
                index = default;
                return false;
        }
    }

    /// <summary>
    /// Compares the index-th entry against the search key. On
    /// <see cref="NodeFlags.OmittedKeys"/> pages the digest is exact and no key bytes
    /// exist, so the comparison reduces to the digest order; everywhere else it is the
    /// full key comparison.
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

    // for debug purpose
    public KeyValuePair<Memory<byte>, Memory<byte>>[] ToArray()
    {
#if NETSTANDARD
        ref var pageReference = ref MemoryMarshal.GetReference(page);
#endif

        var list = new List<KeyValuePair<Memory<byte>, Memory<byte>>>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var meta = GetMeta(i);

            var key = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.Add(ref pageReference, meta.PageOffset),
                meta.KeyLength);

            var value = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.Add(ref pageReference, meta.PageOffset + meta.KeyLength),
                meta.ValueLength);

            list.Add(new KeyValuePair<Memory<byte>, Memory<byte>>(key.ToArray(), value.ToArray()));
        }
        return list.ToArray();
    }

    public string Dump()
    {
        var b =  new StringBuilder();
        var a = ToArray();
        foreach (var (k, v) in a)
        {
            b.AppendLine($"k={Encoding.UTF8.GetString(k.Span)}, v={Encoding.UTF8.GetString(v.Span)}");
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

        // Compact layout: ushort offsets[entryCount + 1] (bit 15 = overflow), then, on
        // pages that store keys, ushort keyLengths[entryCount]. offset[i] and
        // offset[i+1] are adjacent, so one 4-byte load covers both (little endian).
        var offsetPair = Unsafe.ReadUnaligned<uint>(
            ref Unsafe.Add(ref pageReference, metaBase + index * sizeof(ushort)));
        var rawOffset = (ushort)offsetPair;
        var nextOffset = (ushort)(offsetPair >> 16);

        var offset = rawOffset & CompactOffsetMask;
        var keyLength = omittedKeys
            ? (ushort)0
            : Unsafe.ReadUnaligned<ushort>(
                ref Unsafe.Add(ref pageReference,
                    metaBase + (entryCount + 1) * sizeof(ushort) + index * sizeof(ushort)));

        return new NodeEntryMeta
        {
            PageOffset = offset,
            KeyLength = keyLength,
            ValueLength = (rawOffset & CompactOverflowBit) != 0
                ? OverflowSentinel
                : (ushort)((nextOffset & CompactOffsetMask) - offset - keyLength),
        };
    }
}
