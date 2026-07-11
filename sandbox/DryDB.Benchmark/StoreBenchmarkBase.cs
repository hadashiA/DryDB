using System.Buffers.Binary;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using CsSqlite;
using RocksDbSharp;

namespace DryDB.Benchmark;

class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        AddDiagnoser(MemoryDiagnoser.Default);
        AddJob(Job.ShortRun
            .WithWarmupCount(10)
            .WithIterationCount(10)
        );
    }
}

/// <summary>
/// Builds the same 10k-row dataset in SQLite (CsSqlite), DryDB and RocksDB.
/// </summary>
public abstract class StoreBenchmarkBase
{
    protected const int N = 10000;

    protected ReadOnlyDatabase database = default!;
    protected ReadOnlyDatabase databaseRefCounted = default!;

    // sqlite: as-is defaults (command prepared per query)
    protected SqliteConnection cssqliteConnection = default!;

    // sqlite: fair read-only setup (immutable=1, prepared statements reused)
    protected SqliteConnection cssqliteImmutableConnection = default!;

    protected RocksDb rocksDb = default!;

    DirectoryInfo directory = default!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        directory = Directory.CreateTempSubdirectory("drydb_benchmarks");
        var sqlitePath = Path.Combine(directory.FullName, "bench.sqlite");
        var drydbPath = Path.Combine(directory.FullName, "bench.drydb");
        var rocksdbPath = Path.Combine(directory.FullName, "bench.rocksdb");

        // Setup sqlite
        using (var sqlite = new SqliteConnection(sqlitePath))
        {
            sqlite.Open();

            sqlite.ExecuteNonQuery("DROP TABLE IF EXISTS items;");

            sqlite.ExecuteNonQuery("PRAGMA page_size = 4096;");

            sqlite.ExecuteNonQuery(
                """
                CREATE TABLE IF NOT EXISTS items (
                    id INTEGER NOT NULL PRIMARY KEY,
                    data TEXT NOT NULL
                );
                """);

            sqlite.ExecuteNonQuery("BEGIN TRANSACTION;");
            for (var i = 0; i < N; i++)
            {
                sqlite.ExecuteNonQuery(
                    $"""
                     INSERT INTO items (id, data) VALUES ({i}, 'val{i:D10}');
                     """);
            }
            sqlite.ExecuteNonQuery("COMMIT;");
        }

        // Setup DryDB
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

        // Setup RocksDB (big-endian keys so that the default bytewise comparator
        // sorts them numerically, which range scans rely on)
        var rocksDbOptions = new DbOptions().SetCreateIfMissing(true);
        using (var db = RocksDb.Open(rocksDbOptions, rocksdbPath))
        {
            var keyBuffer = new byte[sizeof(long)];
            for (var i = 0; i < N; i++)
            {
                BinaryPrimitives.WriteInt64BigEndian(keyBuffer, i);
                db.Put(keyBuffer, Encoding.UTF8.GetBytes($"val{i:D10}"));
            }
        }

        database = await ReadOnlyDatabase.OpenFileAsync(drydbPath, new DatabaseLoadOptions
        {
        });

        databaseRefCounted = await ReadOnlyDatabase.OpenFileAsync(drydbPath, new DatabaseLoadOptions
        {
            PageReclamation = PageReclamation.ReferenceCounted,
        });

        cssqliteConnection = new SqliteConnection(sqlitePath);
        cssqliteImmutableConnection = new SqliteConnection($"file:{sqlitePath}?immutable=1");
        rocksDb = RocksDb.OpenReadOnly(rocksDbOptions, rocksdbPath, false);

        OnSetup();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        OnCleanup();

        cssqliteConnection.Dispose();
        cssqliteImmutableConnection.Dispose();
        rocksDb.Dispose();
        database.Dispose();
        databaseRefCounted.Dispose();

        try
        {
            directory.Delete(true);
        }
        catch (DirectoryNotFoundException) { }
    }

    protected virtual void OnSetup() { }
    protected virtual void OnCleanup() { }
}
