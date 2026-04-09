using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

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

        var min = 0;
        var max = entryCount;
        var resultIndex = -1;

        while (min < max)
        {
            var midIndex = min + ((max - min) >> 1);
            var midMeta = GetMeta(midIndex);

            ref var ptr = ref Unsafe.Add(ref pageReference, midMeta.PageOffset);
            var midKey = MemoryMarshal.CreateReadOnlySpan(ref ptr, midMeta.KeyLength);

            var compared = keyEncoding.Compare(midKey, key);
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
                    var meta = GetMeta(min);
                    ref var ptr = ref Unsafe.Add(ref pageReference, meta.PageOffset);
                    var foundKey = MemoryMarshal.CreateReadOnlySpan(ref ptr, meta.KeyLength);
                    if (keyEncoding.Compare(foundKey, key) == 0)
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
