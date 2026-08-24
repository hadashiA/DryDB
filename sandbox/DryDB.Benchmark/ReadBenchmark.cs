using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using CsSqlite;
using LightningDB;

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

    [Benchmark]
    public async Task DryDB_FindByKeyAsync()
    {
        for (var i = 0; i < Iterations; i++)
        {
            var table = database.GetTable("items");
            using var _ = await table.GetAsync(FindKey);
        }
    }

    // Pseudo-random key sequence: unlike the fixed-key variants, the binary search
    // branches cannot be learned by the branch predictor.
    [Benchmark]
    public void DryDB_FindByKey_RandomKeys()
    {
        var seed = 123456789u;
        for (var i = 0; i < Iterations; i++)
        {
            seed = seed * 1664525u + 1013904223u; // LCG: cheap, deterministic
            var table = database.GetTable("items");
            using var _ = table.Get((long)(seed % N));
        }
    }

    uint noRepeatSeed = 123456789u;

    // The variant above re-seeds every op, so the same 1000-key sequence repeats and a
    // large branch predictor gradually memorizes its branch history across invocations.
    // Carrying the seed across ops makes the sequence genuinely non-repeating — the
    // closest model of real random access.
    [Benchmark]
    public void DryDB_FindByKey_RandomKeys_NoRepeat()
    {
        var seed = noRepeatSeed;
        for (var i = 0; i < Iterations; i++)
        {
            seed = seed * 1664525u + 1013904223u;
            var table = database.GetTable("items");
            using var _ = table.Get((long)(seed % N));
        }
        noRepeatSeed = seed;
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



    // Parallel reads over spread-out keys: no single hot cache line, which is the
    // realistic shape of concurrent workloads (the same-key variants above are the
    // worst case for refcount line contention).
    [Benchmark]
    public void DryDB_FindByKey_ParallelSpread()
    {
        var tasks = new Task[ThreadCount];
        for (var t = 0; t < ThreadCount; t++)
        {
            var offset = t * 1000;
            tasks[t] = Task.Run(() =>
            {
                var table = database.GetTable("items");
                for (var i = 0; i < Iterations; i++)
                {
                    using var _ = table.Get((offset + i * 7) % N);
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

    // Long-lived read transaction + database handle (see StoreBenchmarkBase);
    // the returned MDBValue is a zero-copy view into the memory map.
    [Benchmark]
    public int LMDB_FindByKey()
    {
        Span<byte> key = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(key, FindKey);

        var total = 0;
        for (var i = 0; i < Iterations; i++)
        {
            var (resultCode, _, value) = lmdbTransaction.Get(lmdbDatabase, key);
            if (resultCode != MDBResultCode.Success) throw new InvalidOperationException(resultCode.ToString());
            total += value.AsSpan().Length;
        }
        return total;
    }
}
