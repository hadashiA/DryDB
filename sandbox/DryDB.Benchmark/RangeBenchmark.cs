using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using CsSqlite;
using LightningDB;

namespace DryDB.Benchmark;

/// <summary>
/// Range scan: 100 consecutive rows per query.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class RangeBenchmark : StoreBenchmarkBase
{
    const int Iterations = 100;
    const long RangeStart = 2000;
    const long RangeEnd = 2099; // inclusive, 100 rows

    SqliteCommand preparedRangeCommand = default!;

    protected override void OnSetup()
    {
        preparedRangeCommand = cssqliteImmutableConnection.CreateCommand(
            "SELECT data FROM items WHERE id >= $a AND id <= $b");
    }

    protected override void OnCleanup()
    {
        preparedRangeCommand.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int DryDB_GetRange()
    {
        Span<byte> startKey = stackalloc byte[sizeof(long)];
        Span<byte> endKey = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(startKey, RangeStart);
        BinaryPrimitives.WriteInt64LittleEndian(endKey, RangeEnd);

        var total = 0;
        for (var i = 0; i < Iterations; i++)
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

    [Benchmark]
    public int CsSqlite_GetRange()
    {
        var total = 0;
        for (var i = 0; i < Iterations; i++)
        {
            using var command = cssqliteConnection.CreateCommand(
                "SELECT data FROM items WHERE id >= $a AND id <= $b");

            command.Parameters.Add("$a", RangeStart);
            command.Parameters.Add("$b", RangeEnd);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                total += reader.GetString(0).Length;
            }
        }
        return total;
    }

    // Fair read-only configuration: immutable=1 + prepared statement reuse
    [Benchmark]
    public int CsSqlite_GetRange_Fair()
    {
        var total = 0;
        for (var i = 0; i < Iterations; i++)
        {
            preparedRangeCommand.Parameters.Add("$a", RangeStart);
            preparedRangeCommand.Parameters.Add("$b", RangeEnd);
            using var reader = preparedRangeCommand.ExecuteReader();
            while (reader.Read())
            {
                total += reader.GetString(0).Length;
            }
        }
        return total;
    }

    [Benchmark]
    public int RocksDB_GetRange()
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
                total += iterator.Value().Length;
            }
        }
        return total;
    }

    [Benchmark]
    public int LMDB_GetRange()
    {
        Span<byte> startKey = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(startKey, RangeStart);

        var total = 0;
        for (var i = 0; i < Iterations; i++)
        {
            using var cursor = lmdbTransaction.CreateCursor(lmdbDatabase);
            var (resultCode, key, value) = cursor.SetRange(startKey) == MDBResultCode.Success
                ? cursor.GetCurrent()
                : (MDBResultCode.NotFound, default, default);
            while (resultCode == MDBResultCode.Success)
            {
                if (BinaryPrimitives.ReadInt64BigEndian(key.AsSpan()) > RangeEnd) break;
                total += value.AsSpan().Length;
                (resultCode, key, value) = cursor.Next();
            }
        }
        return total;
    }
}
