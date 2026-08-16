using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DryDB.Internal;

/// <summary>
/// Comparison-only view of a key encoding, implemented by structs.
/// <see cref="BTree.TreeWalker{TComparer}"/> takes these as value-type generic
/// arguments, so the runtime generates a specialized instantiation per comparer and
/// every comparison in the B+Tree search loops is devirtualized and inlined — including
/// on AOT targets such as IL2CPP, where interface dispatch on
/// <see cref="IKeyEncoding"/> is never devirtualized. <see cref="SupportsKeyDigest"/>
/// additionally becomes a JIT-time constant, which removes the digest branch from the
/// binary search loops entirely.
/// </summary>
interface IKeyComparer
{
    int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b);

    /// <inheritdoc cref="IKeyEncoding.SupportsKeyDigest"/>
    bool SupportsKeyDigest { get; }

    /// <inheritdoc cref="IKeyEncoding.GetKeyDigest"/>
    ulong GetKeyDigest(ReadOnlySpan<byte> key);
}

/// <remarks>
/// Delegates to the sealed encoding singleton: the calls are non-virtual and inlined,
/// and the comparison logic stays in one place.
/// </remarks>
readonly struct Int64KeyComparer : IKeyComparer
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        Int64LittleEndianEncoding.Instance.Compare(a, b);

    public bool SupportsKeyDigest
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetKeyDigest(ReadOnlySpan<byte> key) =>
        Int64LittleEndianEncoding.Instance.GetKeyDigest(key);
}

/// <inheritdoc cref="Int64KeyComparer" path="/remarks"/>
readonly struct AsciiKeyComparer : IKeyComparer
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        AsciiOrdinalEncoding.Instance.Compare(a, b);

    public bool SupportsKeyDigest
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetKeyDigest(ReadOnlySpan<byte> key) =>
        AsciiOrdinalEncoding.Instance.GetKeyDigest(key);
}

#if NET9_0_OR_GREATER
/// <inheritdoc cref="Int64KeyComparer" path="/remarks"/>
readonly struct Uuidv7KeyComparer : IKeyComparer
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        Uuidv7KeyEncoding.Instance.Compare(a, b);

    public bool SupportsKeyDigest
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => false;
    }

    public ulong GetKeyDigest(ReadOnlySpan<byte> key) => 0;
}
#endif

/// <summary>
/// Wraps an arbitrary <see cref="IKeyEncoding"/> (custom/plugin encodings). The inner
/// calls remain interface dispatch, so custom encodings perform exactly as before —
/// no better, no worse.
/// </summary>
readonly struct FallbackKeyComparer(IKeyEncoding encoding) : IKeyComparer
{
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => encoding.Compare(a, b);

    public bool SupportsKeyDigest => encoding.SupportsKeyDigest;

    public ulong GetKeyDigest(ReadOnlySpan<byte> key) => encoding.GetKeyDigest(key);
}

/// <summary>
/// Composite (source key, rid) comparer for non-unique secondary index trees. The
/// rid handling is inlined; the source key comparison goes through
/// <see cref="KeyCompare"/>, which devirtualizes the built-in encodings.
/// </summary>
readonly struct DuplicateKeyComparer(IKeyEncoding sourceEncoding) : IKeyComparer
{
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var aOriginal = a[..^sizeof(int)];
        var bOriginal = b[..^sizeof(int)];
        var sourceResult = KeyCompare.Compare(sourceEncoding, aOriginal, bOriginal);
        if (sourceResult != 0)
        {
            return sourceResult;
        }

        var aValueId = Unsafe.ReadUnaligned<int>(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(a), aOriginal.Length));
        var bValueId = Unsafe.ReadUnaligned<int>(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(b), bOriginal.Length));

        if (aValueId < bValueId) return -1;
        if (aValueId > bValueId) return 1;
        return 0;
    }

    public bool SupportsKeyDigest => sourceEncoding.SupportsKeyDigest;

    // The source key dominates the (source, rid) order, so its digest is a valid
    // coarse digest for the composite key; rid ties collide and fall back.
    public ulong GetKeyDigest(ReadOnlySpan<byte> key) =>
        sourceEncoding.GetKeyDigest(key[..^sizeof(int)]);
}
