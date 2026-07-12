using BenchmarkDotNet.Attributes;

namespace DryDB.Benchmark;

/// <summary>
/// Cold-start cost: open the database file and run the first query.
/// Startup latency and per-open allocations matter for games.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class OpenBenchmark : StoreBenchmarkBase
{
    [Benchmark]
    public async Task<int> DryDB_OpenAndFirstRead()
    {
        using var db = await ReadOnlyDatabase.OpenFileAsync(drydbPath);
        using var result = db.GetTable("items").Get(123L);
        return result.Value.Length;
    }
}
