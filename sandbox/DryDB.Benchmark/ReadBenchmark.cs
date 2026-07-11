using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using CsSqlite;

namespace DryDB.Benchmark;

[Config(typeof(BenchmarkConfig))]
public class ReadBenchmark : StoreBenchmarkBase
{
    const int Iterations = 1000;
    const long FindKey = 123;

    SqliteCommand preparedFindCommand = default!;

    protected override void OnSetup()
    {
        preparedFindCommand = cssqliteImmutableConnection.CreateCommand(
            "SELECT data FROM items WHERE id = $id");
    }

    protected override void OnCleanup()
    {
        preparedFindCommand.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void DryDB_FindByKey()
    {
        for (var i = 0; i < Iterations; i++)
        {
            var table = database.GetTable("items");
            using var _ = table.Get(FindKey);
        }
    }

    const int ThreadCount = 8;

    [Benchmark]
    public void DryDB_FindByKey_Parallel()
    {
        var tasks = new Task[ThreadCount];
        for (var t = 0; t < ThreadCount; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                var table = database.GetTable("items");
                for (var i = 0; i < Iterations; i++)
                {
                    using var _ = table.Get(FindKey);
                }
            });
        }
        Task.WaitAll(tasks);
    }

    [Benchmark]
    public void DryDB_FindByKey_RefCounted()
    {
        for (var i = 0; i < Iterations; i++)
        {
            var table = databaseRefCounted.GetTable("items");
            using var _ = table.Get(FindKey);
        }
    }

    [Benchmark]
    public void DryDB_FindByKey_Parallel_RefCounted()
    {
        var tasks = new Task[ThreadCount];
        for (var t = 0; t < ThreadCount; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                var table = databaseRefCounted.GetTable("items");
                for (var i = 0; i < Iterations; i++)
                {
                    using var _ = table.Get(FindKey);
                }
            });
        }
        Task.WaitAll(tasks);
    }

    [Benchmark]
    public void CsSqlite_FindByKey()
    {
        for (var i = 0; i < Iterations; i++)
        {
            using var command = cssqliteConnection.CreateCommand(
                "SELECT data FROM items WHERE id = $id");

            command.Parameters.Add("$id", FindKey);
            using var reader = command.ExecuteReader();
            reader.Read();
            reader.GetString(0);
        }
    }

    // Fair read-only configuration: immutable=1 + prepared statement reuse
    [Benchmark]
    public void CsSqlite_FindByKey_Fair()
    {
        for (var i = 0; i < Iterations; i++)
        {
            preparedFindCommand.Parameters.Add("$id", FindKey);
            using var reader = preparedFindCommand.ExecuteReader();
            reader.Read();
            reader.GetString(0);
        }
    }

    [Benchmark]
    public int RocksDB_FindByKey()
    {
        var key = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(key, FindKey);

        var total = 0;
        for (var i = 0; i < Iterations; i++)
        {
            var value = rocksDb.Get(key);
            total += value.Length;
        }
        return total;
    }
}
