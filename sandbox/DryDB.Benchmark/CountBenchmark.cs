using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using CsSqlite;
using LightningDB;

namespace DryDB.Benchmark;

/// <summary>
/// Count rows in a range: 8000 rows per query.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class CountBenchmark : StoreBenchmarkBase
{
    const int Iterations = 100;
    const long RangeStart = 1000;
    const long RangeEnd = 8999; // inclusive, 8000 rows

    SqliteCommand preparedCountCommand = default!;

    protected override void OnSetup()
    {
        preparedCountCommand = cssqliteImmutableConnection.CreateCommand(
            "SELECT COUNT(*) FROM items WHERE id >= $a AND id <= $b");
    }

    protected override void OnCleanup()
    {
        preparedCountCommand.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int DryDB_CountRange()
    {
        Span<byte> startKey = stackalloc byte[sizeof(long)];
        Span<byte> endKey = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(startKey, RangeStart);
        BinaryPrimitives.WriteInt64LittleEndian(endKey, RangeEnd);

        var total = 0;
        for (var i = 0; i < Iterations; i++)
        {
            var table = database.GetTable("items");
            total += table.CountRange(startKey, endKey);
        }
        return total;
    }

    [Benchmark]
    public long CsSqlite_CountRange()
    {
        var total = 0L;
        for (var i = 0; i < Iterations; i++)
        {
            using var command = cssqliteConnection.CreateCommand(
                "SELECT COUNT(*) FROM items WHERE id >= $a AND id <= $b");

            command.Parameters.Add("$a", RangeStart);
            command.Parameters.Add("$b", RangeEnd);
            using var reader = command.ExecuteReader();
            reader.Read();
            total += reader.GetInt64(0);
        }
        return total;
    }

    // Fair read-only configuration: immutable=1 + prepared statement reuse
    [Benchmark]
    public long CsSqlite_CountRange_Fair()
    {
        var total = 0L;
        for (var i = 0; i < Iterations; i++)
        {
            preparedCountCommand.Parameters.Add("$a", RangeStart);
            preparedCountCommand.Parameters.Add("$b", RangeEnd);
            using var reader = preparedCountCommand.ExecuteReader();
            reader.Read();
            total += reader.GetInt64(0);
        }
        return total;
    }

    [Benchmark]
    public int RocksDB_CountRange()
    {
        var startKey = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(startKey, RangeStart);

        var total = 0;
        for (var i = 0; i < Iterations; i++)
        {
            using var iterator = rocksDb.NewIterator();
            for (iterator.Seek(startKey); iterator.Valid(); iterator.Next())
            {
                if (BinaryPrimitives.ReadInt64BigEndian(iterator.Key()) > RangeEnd) break;
                total++;
            }
        }
        return total;
    }

    [Benchmark]
    public int LMDB_CountRange()
    {
        Span<byte> startKey = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(startKey, RangeStart);

        var total = 0;
        for (var i = 0; i < Iterations; i++)
        {
            using var cursor = lmdbTransaction.CreateCursor(lmdbDatabase);
            var (resultCode, key, _) = cursor.SetRange(startKey) == MDBResultCode.Success
                ? cursor.GetCurrent()
                : (MDBResultCode.NotFound, default, default);
            while (resultCode == MDBResultCode.Success)
            {
                if (BinaryPrimitives.ReadInt64BigEndian(key.AsSpan()) > RangeEnd) break;
                total++;
                (resultCode, key, _) = cursor.Next();
            }
        }
        return total;
    }
}
