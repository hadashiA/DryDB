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
/// </remarks>
readonly ref struct InternalNodeReader(ReadOnlySpan<byte> page, int entryCount, bool hasKeyDigests)
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
    readonly ReadOnlySpan<byte> page = page;
#else
    readonly ref byte pageReference = ref MemoryMarshal.GetReference(page);
#endif
    readonly int metaBase = DigestBase + (hasKeyDigests ? entryCount * sizeof(ulong) : 0);
    readonly bool hasKeyDigests = hasKeyDigests;

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
        var useDigest = hasKeyDigests && hasKeyDigest;
        int min;
        NodeEntryMeta meta;
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
                if (CompareFull(ref pageReference, min, key, comparer) > 0) break;
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
                        : CompareFull(ref pageReference, mid, key, comparer);
                }
                else
                {
                    cmp = CompareFull(ref pageReference, mid, key, comparer);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    int CompareFull<TComparer>(ref byte pageReference, int index, ReadOnlySpan<byte> key, TComparer comparer)
        where TComparer : struct, IKeyComparer
    {
        var meta = GetMeta(index);
        var entryKey = MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.Add(ref pageReference, meta.PageOffset),
            meta.KeyLength);
        return comparer.Compare(entryKey, key);
    }

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
        ref var ptr = ref Unsafe.Add(
            ref pageReference,
            metaBase + index * Unsafe.SizeOf<NodeEntryMeta>());
        return Unsafe.ReadUnaligned<NodeEntryMeta>(ref ptr);
    }
}
