// One sample of the PMU counters this probe records. The explicit layout pins the
// fields to consecutive 8-byte slots in the same order as EventNames, so the
// indexer can address field i as (base + i * 8) and Pmc.Read can fill the struct
// through the kpep register map without per-field code.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Explicit, Size = 48)]
struct PmcCounters
{
    [FieldOffset(0)] public ulong Cycles;           // FIXED_CYCLES
    [FieldOffset(8)] public ulong Instructions;     // FIXED_INSTRUCTIONS
    [FieldOffset(16)] public ulong Branches;        // INST_BRANCH
    [FieldOffset(24)] public ulong CondBranches;    // INST_BRANCH_COND
    [FieldOffset(32)] public ulong Mispredicts;     // BRANCH_MISPRED_NONSPEC  (_NONSPEC = retired only)
    [FieldOffset(40)] public ulong CondMispredicts; // BRANCH_COND_MISPRED_NONSPEC

    /// <summary>kpep event names, in field-offset order.</summary>
    public static readonly string[] EventNames =
    {
        "FIXED_CYCLES",
        "FIXED_INSTRUCTIONS",
        "INST_BRANCH",
        "INST_BRANCH_COND",
        "BRANCH_MISPRED_NONSPEC",
        "BRANCH_COND_MISPRED_NONSPEC",
    };

    public const int EventCount = 6;

    /// <summary>Counter for <see cref="EventNames"/>[<paramref name="index"/>].</summary>
    public ulong this[int index]
    {
        readonly get => Unsafe.Add(ref Unsafe.AsRef(in Cycles), index);
        set => Unsafe.Add(ref Cycles, index) = value;
    }

    public static PmcCounters operator -(in PmcCounters a, in PmcCounters b)
    {
        var result = default(PmcCounters);
        for (var i = 0; i < EventCount; i++)
        {
            result[i] = a[i] - b[i];
        }
        return result;
    }

    /// <summary>Markdown header cells for the columns this struct renders.</summary>
    public const string MarkdownHeader = "cyc/op | inst/op | branch/op | cond/op | miss/op | condMiss/op | condMiss%";

    public const string MarkdownSeparator = "---:|---:|---:|---:|---:|---:|---:";

    /// <summary>
    /// Renders this sample (typically a delta of two reads) as markdown cells matching
    /// <see cref="MarkdownHeader"/>, normalized to per-operation values.
    /// </summary>
    public readonly string ToMarkdownCells(double operations) =>
        $"{Cycles / operations:F1} | {Instructions / operations:F1} | " +
        $"{Branches / operations:F1} | {CondBranches / operations:F1} | " +
        $"{Mispredicts / operations:F2} | {CondMispredicts / operations:F2} | " +
        $"{100.0 * CondMispredicts / CondBranches:F1}%";
}
