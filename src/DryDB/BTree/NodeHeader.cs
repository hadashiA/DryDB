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

    // Bit 8 was HasKeyDigests in formats 1.1-1.3. Since 1.4 every tree page carries
    // a digest array (digests are mandatory), so the bit is no longer written and the
    // reader rejects pre-1.4 files.

    /// <summary>
    /// The digest array is stored as a complete binary tree in Eytzinger (BFS) order,
    /// padded with <see cref="ulong.MaxValue"/> to 2^k - 1 slots, instead of sorted
    /// order. Introduced in format 1.2.
    /// </summary>
    public const int EytzingerDigests = 1 << 9;

    /// <summary>
    /// Compact entry metadata (introduced in format 1.4). Instead of a fixed-size
    /// record per entry, the meta area holds absolute payload offsets as
    /// <c>ushort[entry_count + 1]</c>: entry i's payload spans
    /// [offset[i], offset[i+1]) and every length is derived from adjacent offsets,
    /// the final slot closing the last entry. Bit 15 of a leaf offset marks the entry
    /// as an overflow value (its inline payload is the 8-byte blob page ordinal),
    /// which is why this layout requires pageSize &lt;= 32767; the builder keeps the
    /// classic meta records for larger pages. Leaf pages that store keys append
    /// <c>ushort key_length[entry_count]</c> after the offsets. Internal pages never
    /// need key lengths (payload per entry is key + 8-byte child ordinal, so the key
    /// length falls out of the offsets).
    /// </summary>
    public const int CompactMeta = 1 << 10;

    /// <summary>
    /// The payload stores no key bytes (introduced in format 1.4). Only written when
    /// the key encoding declares <see cref="IKeyEncoding.IsKeyDigestExact"/>: the
    /// digest array is then a bijective image of the keys, so it replaces every key
    /// comparison (equal digest = equal key) and reconstructs key bytes via
    /// <see cref="IKeyEncoding.TryDecodeKeyFromDigest"/>. Always combined with
    /// <see cref="CompactMeta"/>, never with <see cref="EytzingerDigests"/>
    /// (enumeration needs the digest of the i-th entry in sorted order). Internal
    /// pages drop their meta area entirely: the payload is a dense array of 8-byte
    /// child ordinals.
    /// </summary>
    public const int OmittedKeys = 1 << 11;
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

    public bool HasEytzingerDigests
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ((int)Kind & NodeFlags.EytzingerDigests) != 0;
    }

    public bool HasCompactMeta
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ((int)Kind & NodeFlags.CompactMeta) != 0;
    }

    public bool HasOmittedKeys
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ((int)Kind & NodeFlags.OmittedKeys) != 0;
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

