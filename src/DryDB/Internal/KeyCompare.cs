using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DryDB.Internal;

/// <summary>
/// Guarded devirtualization for the built-in key encodings.
/// B+Tree search compares keys O(log n) times per node; going through
/// <see cref="IKeyEncoding.Compare(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/> costs an
/// interface dispatch per comparison, which dominates on cached pages
/// (and is never devirtualized on AOT targets such as IL2CPP).
/// </summary>
static class KeyCompare
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Compare(IKeyEncoding keyEncoding, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (ReferenceEquals(keyEncoding, Int64LittleEndianEncoding.Instance))
        {
            var na = Unsafe.ReadUnaligned<long>(ref MemoryMarshal.GetReference(a));
            var nb = Unsafe.ReadUnaligned<long>(ref MemoryMarshal.GetReference(b));
            return (na > nb ? 1 : 0) - (na < nb ? 1 : 0);
        }
        if (ReferenceEquals(keyEncoding, AsciiOrdinalEncoding.Instance))
        {
            return a.SequenceCompareTo(b);
        }
        return keyEncoding.Compare(a, b);
    }
}
