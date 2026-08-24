// PMU probe for DryDB lookups on Apple Silicon.
// Reads per-thread performance counters (cycles, instructions, branches,
// branch mispredicts) around the same lookup loops as ReadBenchmark, for
// three node layouts x three key patterns.
//
// Uses the private kperf/kperfdata frameworks (same machinery as Instruments),
// which requires root: see README.md in this directory.
// Ported from the well-known kpc_demo.c (ibireme).

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DryDB;

const int N = 10_000;
const int Iterations = 1000;          // lookups per batch (matches ReadBenchmark)
const int WarmupBatches = 3000;       // reach tier-1 JIT and steady state
const int MeasureBatches = 2000;

var dir = Directory.CreateTempSubdirectory("drydb_pmc");
try
{
    var dbs = new (string Layout, ReadOnlyDatabase Db)[]
    {
        ("sorted+simd", await BuildAsync(Path.Combine(dir.FullName, "sorted.drydb"), eytzinger: false, digests: true)),
        ("eytzinger", await BuildAsync(Path.Combine(dir.FullName, "eytz.drydb"), eytzinger: true, digests: true)),
        ("no-digest", await BuildAsync(Path.Combine(dir.FullName, "nodig.drydb"), eytzinger: false, digests: false)),
    };

    Pmc.Init();
    Console.Error.WriteLine("counters armed; measuring...");
    MeasureAll(dbs);

    foreach (var (_, db) in dbs) db.Dispose();
}
finally
{
    try { dir.Delete(true); } catch { }
}
return;

// ---------------------------------------------------------------- local functions

async Task<ReadOnlyDatabase> BuildAsync(string path, bool eytzinger, bool digests)
{
    using (var builder = new DatabaseBuilder { PageSize = 4096, KeyDigests = digests, EytzingerDigests = eytzinger })
    {
        var t = builder.CreateTable("items", KeyEncoding.Int64LittleEndian);
        for (var i = 0; i < N; i++)
        {
            t.Append(i, Encoding.UTF8.GetBytes($"val{i:D10}"));
        }
        await builder.BuildToFileAsync(path);
    }
    return await ReadOnlyDatabase.OpenFileAsync(path);
}

void MeasureAll((string Layout, ReadOnlyDatabase Db)[] dbs)
{
    string[] patterns = { "fixed", "repeat1000", "norepeat" };

    Console.WriteLine("| layout | keys | ns/op | cyc/op | inst/op | branch/op | cond/op | miss/op | condMiss/op | condMiss% |");
    Console.WriteLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");

    var before = new ulong[Pmc.EventCount];
    var after = new ulong[Pmc.EventCount];

    foreach (var (layout, db) in dbs)
    {
        var table = db.GetTable("items");
        for (var p = 0; p < patterns.Length; p++)
        {
            var seed = 123456789u;
            for (var w = 0; w < WarmupBatches; w++)
            {
                seed = RunBatch(table, p, seed);
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var gc0 = GC.CollectionCount(0);

            Pmc.Read(before);
            var sw = Stopwatch.StartNew();
            for (var m = 0; m < MeasureBatches; m++)
            {
                seed = RunBatch(table, p, seed);
            }
            sw.Stop();
            Pmc.Read(after);

            if (GC.CollectionCount(0) != gc0) Console.Error.WriteLine($"warning: GC ran during {layout}/{patterns[p]}");

            var ops = (double)MeasureBatches * Iterations;
            var missPct = 100.0 * (after[5] - before[5]) / (after[3] - before[3]);
            Console.WriteLine(
                $"| {layout} | {patterns[p]} | {sw.Elapsed.TotalNanoseconds / ops:F1} | {Delta(0):F1} | {Delta(1):F1} | " +
                $"{Delta(2):F1} | {Delta(3):F1} | {Delta(4):F2} | {Delta(5):F2} | {missPct:F1}% |");

            double Delta(int i) => (after[i] - before[i]) / ops;
        }
    }
}

// Returns final seed so the JIT can't dead-code the loop; patterns match ReadBenchmark.
static uint RunBatch(ReadOnlyTable table, int pattern, uint seed)
{
    switch (pattern)
    {
        case 0: // fixed key
            for (var i = 0; i < Iterations; i++)
            {
                using var _ = table.Get(123L);
            }
            return seed;
        case 1: // memorizable: same sequence every batch
            seed = 123456789u;
            goto case 2;
        case 2: // no-repeat: seed carried across batches
            for (var i = 0; i < Iterations; i++)
            {
                seed = seed * 1664525u + 1013904223u;
                using var _ = table.Get((long)(seed % N));
            }
            return seed;
        default:
            throw new ArgumentOutOfRangeException(nameof(pattern));
    }
}

// ---------------------------------------------------------------- kperf interop

static class Kpc
{
    const string Kperf = "/System/Library/PrivateFrameworks/kperf.framework/kperf";
    const string KperfData = "/System/Library/PrivateFrameworks/kperfdata.framework/kperfdata";

    public const int MaxCounters = 32;

    [DllImport(Kperf)] public static extern int kpc_force_all_ctrs_set(int val);
    [DllImport(Kperf)] public static unsafe extern int kpc_set_config(uint classes, ulong* config);
    [DllImport(Kperf)] public static extern int kpc_set_counting(uint classes);
    [DllImport(Kperf)] public static extern int kpc_set_thread_counting(uint classes);
    [DllImport(Kperf)] public static unsafe extern int kpc_get_thread_counters(uint tid, uint bufCount, ulong* buf);

    [DllImport(KperfData)] public static unsafe extern int kpep_db_create(byte* name, out IntPtr db);
    [DllImport(KperfData)] public static extern int kpep_config_create(IntPtr db, out IntPtr cfg);
    [DllImport(KperfData)] public static extern int kpep_config_force_counters(IntPtr cfg);
    [DllImport(KperfData)] public static unsafe extern int kpep_db_event(IntPtr db, byte* name, out IntPtr ev);
    [DllImport(KperfData)] public static unsafe extern int kpep_config_add_event(IntPtr cfg, ref IntPtr ev, uint flag, uint* err);
    [DllImport(KperfData)] public static unsafe extern int kpep_config_kpc(IntPtr cfg, ulong* buf, nuint bufSizeBytes);
    [DllImport(KperfData)] public static extern int kpep_config_kpc_count(IntPtr cfg, out nuint count);
    [DllImport(KperfData)] public static extern int kpep_config_kpc_classes(IntPtr cfg, out uint classes);
    [DllImport(KperfData)] public static unsafe extern int kpep_config_kpc_map(IntPtr cfg, nuint* buf, nuint bufSizeBytes);
}

static unsafe class Pmc
{
    static readonly string[] Events =
    [
        "FIXED_CYCLES",                  // 0
        "FIXED_INSTRUCTIONS",            // 1
        "INST_BRANCH",                   // 2
        "INST_BRANCH_COND",              // 3
        "BRANCH_MISPRED_NONSPEC",        // 4
        "BRANCH_COND_MISPRED_NONSPEC" // 5
    ];

    public static int EventCount => Events.Length;

    static readonly nuint[] map = new nuint[Events.Length];

    public static void Init()
    {
        Check(Kpc.kpep_db_create(null, out var db), "kpep_db_create");
        Check(Kpc.kpep_config_create(db, out var cfg), "kpep_config_create");
        Check(Kpc.kpep_config_force_counters(cfg), "kpep_config_force_counters");

        foreach (var name in Events)
        {
            var utf8 = Encoding.UTF8.GetBytes(name + "\0");
            fixed (byte* p = utf8)
            {
                Check(Kpc.kpep_db_event(db, p, out var ev), $"kpep_db_event({name})");
                uint err = 0;
                Check(Kpc.kpep_config_add_event(cfg, ref ev, 0, &err), $"kpep_config_add_event({name}) err={err}");
            }
        }

        Check(Kpc.kpep_config_kpc_classes(cfg, out var classes), "kpep_config_kpc_classes");
        Check(Kpc.kpep_config_kpc_count(cfg, out var regCount), "kpep_config_kpc_count");
        fixed (nuint* m = map)
        {
            Check(Kpc.kpep_config_kpc_map(cfg, m, (nuint)(map.Length * sizeof(nuint))), "kpep_config_kpc_map");
        }
        var regs = stackalloc ulong[Kpc.MaxCounters];
        Check(Kpc.kpep_config_kpc(cfg, regs, regCount * sizeof(ulong)), "kpep_config_kpc");

        // Root required from here on.
        Check(Kpc.kpc_force_all_ctrs_set(1), "kpc_force_all_ctrs_set (run with sudo?)");
        Check(Kpc.kpc_set_config(classes, regs), "kpc_set_config");
        Check(Kpc.kpc_set_counting(classes), "kpc_set_counting");
        Check(Kpc.kpc_set_thread_counting(classes), "kpc_set_thread_counting");
    }

    public static void Read(ulong[] values) // values.Length == EventCount
    {
        var buf = stackalloc ulong[Kpc.MaxCounters];
        var ret = Kpc.kpc_get_thread_counters(0, Kpc.MaxCounters, buf);
        if (ret != 0) throw new Exception($"kpc_get_thread_counters failed: {ret}");
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = buf[map[i]];
        }
    }

    static void Check(int ret, string what)
    {
        if (ret != 0) throw new Exception($"{what} failed: {ret}");
    }
}
