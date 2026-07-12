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
readonly ref struct InternalNodeReader(ReadOnlySpan<byte> page, int entryCount)
{
    [StructLayout(LayoutKind.Explicit, Size = 6, Pack = 1)]
    struct NodeEntryMeta
    {
        [FieldOffset(0)]
        public int PageOffset;

        [FieldOffset(4)]
        public ushort KeyLength;
    }

#if NETSTANDARD
    readonly ReadOnlySpan<byte> page = page;
#else
    readonly ref byte pageReference = ref MemoryMarshal.GetReference(page);
#endif

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

    public bool TrySearch(ReadOnlySpan<byte> key, IKeyEncoding keyEncoding, out PageNumber childPageNumber)
    {
#if NETSTANDARD
        ref var pageReference = ref MemoryMarshal.GetReference(page);
#endif
        // Branchless upper-bound search (see LeafNodeReader.TrySearch): the probe moves
        // right while probeKey <= key, and `first += cond ? half : 0` compiles to a
        // conditional select instead of an unpredictable branch.
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
            first += compared <= 0 ? half : 0;
            length -= half;
        }

        var lastMeta = GetMeta(first);
        var lastKey = MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.Add(ref pageReference, lastMeta.PageOffset),
            lastMeta.KeyLength);
        first += KeyCompare.Compare(keyEncoding, lastKey, key) <= 0 ? 1 : 0;

        // The child to descend into is the entry before the upper bound.
        var index = first == 0 ? 0 : first - 1;
        var childMeta = GetMeta(index);
        childPageNumber = Unsafe.ReadUnaligned<PageNumber>(
            ref Unsafe.Add(
                ref pageReference,
                childMeta.PageOffset + childMeta.KeyLength));
        return true;
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
            Unsafe.SizeOf<PageHeader>() + Unsafe.SizeOf<NodeHeader>() +
            index * Unsafe.SizeOf<NodeEntryMeta>());
        return Unsafe.ReadUnaligned<NodeEntryMeta>(ref ptr);
    }
}