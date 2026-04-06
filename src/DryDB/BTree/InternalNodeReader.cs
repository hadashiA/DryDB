using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

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
        var min = 0;
        var max = entryCount;

        NodeEntryMeta meta;
        while (min < max)
        {
            var mid = min + ((max - min) >> 1);

            meta = GetMeta(mid);
            ref var ptr = ref Unsafe.Add(ref pageReference, meta.PageOffset);

            var midKey = MemoryMarshal.CreateReadOnlySpan(ref ptr, meta.KeyLength);
            var cmp = keyEncoding.Compare(midKey, key);
            if (cmp <= 0) // upper bounds
            {
                min = mid + 1;
            }
            else
            {
                max = mid;
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