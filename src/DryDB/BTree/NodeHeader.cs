using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DryDB.BTree;

enum NodeKind
{
    Leaf = 0,
    Internal = 1,
}

static class NodeFlags
{
    // The low byte of the on-disk kind field is the NodeKind; the upper bits carry
    // per-page format flags. Old files have no flags set and keep the old layout.
    public const int KindMask = 0xFF;

    /// <summary>
    /// A contiguous array of 8-byte key digests sits between the node header and the
    /// entry metadata.
    /// </summary>
    public const int HasKeyDigests = 1 << 8;
}

static class NodeHeaderExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NodeHeader GetNodeHeader(this IPageEntry page) =>
        NodeHeader.Parse(page.Memory.Span);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetEntryCount(this IPageEntry page) =>
        NodeHeader.ParseEntryCount(page.Memory.Span);
}

/// <summary>
///
/// </summary>
/// <remarks>
///  This implementation support only for little endian
/// </remarks>
[StructLayout(LayoutKind.Explicit, Pack = 1)]
unsafe struct NodeHeader
{
    // Raw on-disk kind field: low byte is the NodeKind, upper bits are NodeFlags.
    [FieldOffset(0)]
    public NodeKind Kind;

    public NodeKind NodeKind
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (NodeKind)((int)Kind & NodeFlags.KindMask);
    }

    public bool HasKeyDigests
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ((int)Kind & NodeFlags.HasKeyDigests) != 0;
    }

    [FieldOffset(4)]
    public fixed byte EntryCountBytes[4];

    [FieldOffset(4)]
    public int EntryCount;

    [FieldOffset(8)]
    public fixed byte LeftSiblingPositionBytes[8];

    [FieldOffset(8)]
    public PageNumber LeftSiblingPageNumber;

    [FieldOffset(16)]
    public fixed byte RightSiblingPageNumberBytes[8];

    [FieldOffset(16)]
    public PageNumber RightSiblingPageNumber;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NodeHeader Parse(ReadOnlySpan<byte> page)
    {
        return Unsafe.ReadUnaligned<NodeHeader>(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(page), Unsafe.SizeOf<PageHeader>()));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ParseEntryCount(ReadOnlySpan<byte> page)
    {
        return Unsafe.ReadUnaligned<int>(
            ref Unsafe.Add(
                ref MemoryMarshal.GetReference(page), 8));
    }
}

