using System.Text;
using BenchmarkDotNet.Attributes;

namespace DryDB.Benchmark;

/// <summary>
/// Reads against a 1M-row table (~35 MB): unlike the 10k benchmarks, the tree is
/// three levels deep and the working set does not fit in L2, so per-probe cache
/// behaviour dominates.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class BigReadBenchmark
{
    const int N = 1_000_000;
    const int Iterations = 1000;
    const long FindKey = 123_456;

    DirectoryInfo directory = default!;
    ReadOnlyDatabase database = default!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        directory = Directory.CreateTempSubdirectory("drydb_benchmarks_big");
        var drydbPath = Path.Combine(directory.FullName, "big.drydb");

        using (var builder = new DatabaseBuilder
               {
                   PageSize = 4096,
               })
        {
            var tableBuilder = builder.CreateTable("items", KeyEncoding.Int64LittleEndian);
            for (var i = 0; i < N; i++)
            {
                tableBuilder.Append(i, Encoding.UTF8.GetBytes($"val{i:D10}"));
            }
            await builder.BuildToFileAsync(drydbPath);
        }

        database = await ReadOnlyDatabase.OpenFileAsync(drydbPath);

        // Touch every key once so that reads measure the cached hot path.
        var table = database.GetTable("items");
        for (var i = 0L; i < N; i += 50)
        {
            using var _ = table.Get(i);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        database.Dispose();
        try
        {
            directory.Delete(true);
        }
        catch (DirectoryNotFoundException) { }
    }

    [Benchmark(Baseline = true)]
    public void DryDB_FindByKey_1M()
    {
        for (var i = 0; i < Iterations; i++)
        {
            var table = database.GetTable("items");
            using var _ = table.Get(FindKey);
        }
    }

    [Benchmark]
    public void DryDB_FindByKey_1M_RandomKeys()
    {
        var seed = 123456789u;
        for (var i = 0; i < Iterations; i++)
        {
            seed = seed * 1664525u + 1013904223u;
            var table = database.GetTable("items");
            using var _ = table.Get((long)(seed % N));
        }
    }

    [Benchmark]
    public int DryDB_GetRange_1M()
    {
        Span<byte> startKey = stackalloc byte[sizeof(long)];
        Span<byte> endKey = stackalloc byte[sizeof(long)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(startKey, 500_000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(endKey, 500_099);

        var total = 0;
        for (var i = 0; i < 100; i++)
        {
            var table = database.GetTable("items");
            using var result = table.GetRange(startKey, endKey);
            foreach (var value in result)
            {
                total += value.Length;
            }
        }
        return total;
    }
}
