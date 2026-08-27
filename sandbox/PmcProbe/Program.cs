// PMU probe for DryDB lookups on Apple Silicon.
// Reads per-thread performance counters (cycles, instructions, branches,
// branch mispredicts) around the same lookup loops as ReadBenchmark, for
// three node layouts x three key patterns.
//
// PMU access lives in Kpc.cs (P/Invoke surface) and Pmc.cs (event setup / reads);
// requires root — see README.md in this directory.

using System.Diagnostics;
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

    Console.WriteLine($"| layout | keys | ns/op | {PmcCounters.MarkdownHeader} |");
    Console.WriteLine($"|---|---|---:|{PmcCounters.MarkdownSeparator}|");

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

            var before = Pmc.Read();
            var sw = Stopwatch.StartNew();
            for (var m = 0; m < MeasureBatches; m++)
            {
                seed = RunBatch(table, p, seed);
            }
            sw.Stop();
            var delta = Pmc.Read() - before;

            if (GC.CollectionCount(0) != gc0) Console.Error.WriteLine($"warning: GC ran during {layout}/{patterns[p]}");

            var ops = (double)MeasureBatches * Iterations;
            Console.WriteLine(
                $"| {layout} | {patterns[p]} | {sw.Elapsed.TotalNanoseconds / ops:F1} | {delta.ToMarkdownCells(ops)} |");
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
