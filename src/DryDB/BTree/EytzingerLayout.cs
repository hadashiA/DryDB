using System;
using System.Runtime.CompilerServices;

namespace DryDB.BTree;

/// <summary>
/// Digest array stored as a complete binary tree in Eytzinger (BFS) order.
/// </summary>
/// <remarks>
/// The array is padded with <see cref="ulong.MaxValue"/> up to 2^k - 1 slots so the
/// tree is complete, which makes the descent fully branch-free — each level is
/// <c>i = 2i + (digest &lt; key)</c> with no mispredictable branch — and gives a
/// closed-form rank: after the descent, <c>rank = i - size - 1</c> counts exactly the
/// digests below the probe. The padding acts as +infinity (a real digest can also be
/// MaxValue; it then simply lands at the end of the order, which is still correct).
/// The top of the tree sits in the first cache line, so the levels that a sorted
/// binary search scatters across the page are clustered here.
/// </remarks>
static class EytzingerLayout
{
    /// <summary>
    /// The smallest complete-tree slot count (2^k - 1) holding <paramref name="count"/> digests.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CompleteSize(int count)
    {
        var m = 1;
        while (m < count + 1) m <<= 1;
        return m - 1;
    }

    /// <summary>
    /// Returns the number of digests strictly less than <paramref name="keyDigest"/>
    /// (the lower-bound rank in sorted order). <paramref name="completeSize"/> is the
    /// padded slot count; slot i (1-indexed) lives at
    /// <paramref name="digestBase"/> + (i - 1) * 8.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LowerBoundRank(ref byte pageReference, int digestBase, int completeSize, ulong keyDigest)
    {
        var i = 1;
        while (i <= completeSize)
        {
            var digest = Unsafe.ReadUnaligned<ulong>(
                ref Unsafe.Add(ref pageReference, digestBase + (i - 1) * sizeof(ulong)));
            i = 2 * i + (digest < keyDigest ? 1 : 0);
        }
        return i - completeSize - 1;
    }

    /// <summary>
    /// Scatters sorted digests into Eytzinger slots (length must be
    /// <see cref="CompleteSize"/> of the digest count); unused slots become MaxValue.
    /// </summary>
    public static void Scatter(ReadOnlySpan<ulong> sortedDigests, Span<ulong> slots)
    {
        var k = 0;
        FillInOrder(sortedDigests, slots, 1, ref k);
    }

    static void FillInOrder(ReadOnlySpan<ulong> sortedDigests, Span<ulong> slots, int i, ref int k)
    {
        if (i > slots.Length) return;
        FillInOrder(sortedDigests, slots, 2 * i, ref k);
        slots[i - 1] = k < sortedDigests.Length ? sortedDigests[k++] : ulong.MaxValue;
        FillInOrder(sortedDigests, slots, 2 * i + 1, ref k);
    }
}
