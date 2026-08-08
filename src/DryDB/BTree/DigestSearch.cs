using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif

namespace DryDB.BTree;

/// <summary>
/// Lower-bound search over the contiguous key digest array of a node.
/// </summary>
/// <remarks>
/// The tail levels of a binary search carry the unpredictable branches (a ~50%
/// mispredict per level on non-repeating random keys). This kernel runs the branchy
/// binary search only down to a 32-element window and finishes with a branch-free SIMD
/// count, which removes those mispredicts. A full SIMD scan would touch every cache
/// line of the digest array and loses when the node is out of cache; the hybrid wins
/// in both regimes on unpredictable keys.
/// </remarks>
static class DigestSearch
{
    /// <summary>
    /// Whether the SIMD window path is available. The lower-bound restructure only
    /// pays off together with the SIMD window (losing the classic search's early
    /// digest-equality exit costs more than the restructure alone gains), so callers
    /// must keep using the classic mixed digest binary search when this is false —
    /// notably on netstandard targets, which have no <c>Vector128</c>.
    /// </summary>
#if NET8_0_OR_GREATER
    public static bool IsAccelerated
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector128.IsHardwareAccelerated;
    }
#else
    public const bool IsAccelerated = false;
#endif

    /// <summary>
    /// Returns the number of digests strictly less than <paramref name="keyDigest"/>,
    /// i.e. the index of the first digest &gt;= <paramref name="keyDigest"/> (or
    /// <paramref name="count"/> if there is none).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LowerBound(ref byte pageReference, int digestBase, int count, ulong keyDigest)
    {
        var min = 0;
        var max = count;
        while (max - min > 32)
        {
            var mid = min + ((max - min) >> 1);
            var digest = Unsafe.ReadUnaligned<ulong>(
                ref Unsafe.Add(ref pageReference, digestBase + mid * sizeof(ulong)));
            if (digest < keyDigest)
            {
                min = mid + 1;
            }
            else
            {
                max = mid;
            }
        }

#if NET8_0_OR_GREATER
        if (Vector128.IsHardwareAccelerated && max >= 32)
        {
            // Anchor the 32-wide window at [max-32, max): it stays inside the array,
            // and every element below `min` it may cover is < keyDigest by the binary
            // search invariant, so counting them keeps the result exact — no sentinels
            // or masking needed.
            var start = max - 32;
            var keyVec = Vector128.Create(keyDigest);
            // Four independent accumulators: a single accumulator would serialize the
            // sixteen subtracts into one long dependency chain, whose latency exceeds
            // the mispredict cost it replaces.
            var acc0 = Vector128<ulong>.Zero;
            var acc1 = Vector128<ulong>.Zero;
            var acc2 = Vector128<ulong>.Zero;
            var acc3 = Vector128<ulong>.Zero;
            ref var window = ref Unsafe.Add(ref pageReference, digestBase + start * sizeof(ulong));
            for (var i = 0; i < 32 * sizeof(ulong); i += 8 * sizeof(ulong))
            {
                // a true lane is all-ones (= -1): accumulate counts by subtraction
                acc0 -= Vector128.LessThan(Unsafe.ReadUnaligned<Vector128<ulong>>(ref Unsafe.Add(ref window, i)), keyVec);
                acc1 -= Vector128.LessThan(Unsafe.ReadUnaligned<Vector128<ulong>>(ref Unsafe.Add(ref window, i + 2 * sizeof(ulong))), keyVec);
                acc2 -= Vector128.LessThan(Unsafe.ReadUnaligned<Vector128<ulong>>(ref Unsafe.Add(ref window, i + 4 * sizeof(ulong))), keyVec);
                acc3 -= Vector128.LessThan(Unsafe.ReadUnaligned<Vector128<ulong>>(ref Unsafe.Add(ref window, i + 6 * sizeof(ulong))), keyVec);
            }
            var acc = (acc0 + acc1) + (acc2 + acc3);
            return start + (int)(acc.GetElement(0) + acc.GetElement(1));
        }
#endif

        while (min < max)
        {
            var mid = min + ((max - min) >> 1);
            var digest = Unsafe.ReadUnaligned<ulong>(
                ref Unsafe.Add(ref pageReference, digestBase + mid * sizeof(ulong)));
            if (digest < keyDigest)
            {
                min = mid + 1;
            }
            else
            {
                max = mid;
            }
        }
        return min;
    }
}
