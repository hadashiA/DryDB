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
/// </remarks>
readonly ref struct LeafNodeReader(ReadOnlySpan<byte> page, int entryCount)
{
    internal const ushort OverflowSentinel = ushort.MaxValue; // 0xFFFF

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsOverflow(ushort valueLength) => valueLength == OverflowSentinel;

    [StructLayout(LayoutKind.Explicit, Size = 6, Pack = 1)]
    struct NodeEntryMeta
    {
        [FieldOffset(0)]
        public int PageOffset;

        [FieldOffset(4)]
        public ushort KeyLength;

        [FieldOffset(6)]
        public ushort ValueLength;
    }

#if NETSTANDARD
    readonly ReadOnlySpan<byte> page = page;
#else
    readonly ref byte pageReference = ref MemoryMarshal.GetReference(page);
#endif

    public void GetAt(int index, out ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value)
    {
#if NETSTANDARD
        ref var pageReference = ref MemoryMarshal.GetReference(page);
#endif
        var meta = GetMeta(index);
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

    public bool TryFindValue(
        scoped ReadOnlySpan<byte> key,
        IKeyEncoding keyEncoding,
        out int index,
        out int valueOffset,
        out ushort valueLength)
    {
        if (TrySearch(key, SearchOperator.Equal, keyEncoding, out index))
        {
            var meta = GetMeta(index);
            valueOffset = meta.PageOffset + meta.KeyLength;
            valueLength = meta.ValueLength;
            return true;
        }

        valueOffset = default;
        valueLength = default;
        return false;
    }

    public bool TrySearch(
        ReadOnlySpan<byte> key,
        SearchOperator op,
        IKeyEncoding keyEncoding,
        out int index)
    {
#if  NETSTANDARD
        ref var pageReference = ref MemoryMarshal.GetReference(page);
#endif
        if (entryCount <= 0)
        {
            index = default;
            return false;
        }

        // Branchless bound search: `first += cond ? half : 0` compiles to a conditional
        // select, so a random key doesn't pay a branch misprediction per probe.
        // The probe moves right while probeKey < key (LowerBound/Equal, boundary -1)
        // or probeKey <= key (UpperBound, boundary 0).
        var boundary = op == SearchOperator.UpperBound ? 0 : -1;

        var first = 0;
        var length = entryCount;
        while (length > 1)
        {
            var half = length >> 1;
            var meta = GetMeta(first + half - 1);
            var probeKey = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.Add(ref pageReference, meta.PageOffset),
                meta.KeyLength);

            var compared = KeyCompare.Compare(keyEncoding, probeKey, key);
            first += compared <= boundary ? half : 0;
            length -= half;
        }

        var lastMeta = GetMeta(first);
        var lastKey = MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.Add(ref pageReference, lastMeta.PageOffset),
            lastMeta.KeyLength);
        var lastCompared = KeyCompare.Compare(keyEncoding, lastKey, key);

        if (op == SearchOperator.Equal)
        {
            if (lastCompared == 0)
            {
                index = first;
                return true;
            }
            if (lastCompared < 0 && first + 1 < entryCount)
            {
                // The bound landed one before the candidate; check it.
                var nextMeta = GetMeta(first + 1);
                var nextKey = MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.Add(ref pageReference, nextMeta.PageOffset),
                    nextMeta.KeyLength);
                if (KeyCompare.Compare(keyEncoding, nextKey, key) == 0)
                {
                    index = first + 1;
                    return true;
                }
            }
            index = default;
            return false;
        }

        first += lastCompared <= boundary ? 1 : 0;
        index = first;
        return first < entryCount;
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
        ref var ptr = ref Unsafe.Add(
            ref pageReference,
            Unsafe.SizeOf<PageHeader>() + Unsafe.SizeOf<NodeHeader>() +
            index * Unsafe.SizeOf<NodeEntryMeta>());
        return Unsafe.ReadUnaligned<NodeEntryMeta>(ref ptr);
    }
}
