using System.Buffers.Binary;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using CsSqlite;
using LightningDB;
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
/// Builds the same 10k-row dataset in SQLite (CsSqlite), DryDB, RocksDB and LMDB.
/// </summary>
public abstract class StoreBenchmarkBase
{
    protected const int N = 10000;

    protected ReadOnlyDatabase database = default!;

    // sqlite: as-is defaults (command prepared per query)
    protected SqliteConnection cssqliteConnection = default!;

    // sqlite: fair read-only setup (immutable=1, prepared statements reused)
    protected SqliteConnection cssqliteImmutableConnection = default!;

    protected RocksDb rocksDb = default!;

    // lmdb: read-only environment with a single long-lived read transaction and
    // database handle, which is the standard fast read pattern for LMDB (a read
    // txn pins a snapshot; values returned are zero-copy views into the mmap).
    protected LightningEnvironment lmdbEnvironment = default!;
    protected LightningTransaction lmdbTransaction = default!;
    protected LightningDatabase lmdbDatabase = default!;

    protected string drydbPath = default!;

    DirectoryInfo directory = default!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        directory = Directory.CreateTempSubdirectory("drydb_benchmarks");
        var sqlitePath = Path.Combine(directory.FullName, "bench.sqlite");
        drydbPath = Path.Combine(directory.FullName, "bench.drydb");
        var rocksdbPath = Path.Combine(directory.FullName, "bench.rocksdb");
        var lmdbPath = Path.Combine(directory.FullName, "bench.lmdb");

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

        // Setup LMDB (big-endian keys so that the default lexicographic comparator
        // sorts them numerically, same as RocksDB above). 4KB pages to match the
        // other stores (the bundled LMDB would otherwise use the OS page size,
        // which is 16KB on Apple Silicon).
        Directory.CreateDirectory(lmdbPath);
        using (var env = new LightningEnvironment(lmdbPath, new EnvironmentConfiguration
               {
                   MapSize = 64L * 1024 * 1024,
                   PageSize = 4096,
               }))
        {
            env.Open();
            using var txn = env.BeginTransaction();
            using var db = txn.OpenDatabase();
            var keyBuffer = new byte[sizeof(long)];
            for (var i = 0; i < N; i++)
            {
                BinaryPrimitives.WriteInt64BigEndian(keyBuffer, i);
                txn.Put(db, keyBuffer, Encoding.UTF8.GetBytes($"val{i:D10}"));
            }
            txn.Commit();
        }

        database = await ReadOnlyDatabase.OpenFileAsync(drydbPath, new DatabaseLoadOptions
        {
        });

        cssqliteConnection = new SqliteConnection(sqlitePath);
        cssqliteImmutableConnection = new SqliteConnection($"file:{sqlitePath}?immutable=1");
        rocksDb = RocksDb.OpenReadOnly(rocksDbOptions, rocksdbPath, false);

        // Re-open LMDB read-only. ReadOnly: mmap the file PROT_READ (no write
        // txns possible). NoLock: skip the reader-table / lock-file machinery,
        // which is the usual choice for a single-process read-only consumer;
        // it only affects transaction begin/end, which is outside the measured
        // loop anyway because the read transaction is reused for every op.
        lmdbEnvironment = new LightningEnvironment(lmdbPath, new EnvironmentConfiguration
        {
            MapSize = 64L * 1024 * 1024,
        });
        lmdbEnvironment.Open(EnvironmentOpenFlags.ReadOnly | EnvironmentOpenFlags.NoLock);
        lmdbTransaction = lmdbEnvironment.BeginTransaction(TransactionBeginFlags.ReadOnly);
        lmdbDatabase = lmdbTransaction.OpenDatabase();

        OnSetup();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        OnCleanup();

        cssqliteConnection.Dispose();
        cssqliteImmutableConnection.Dispose();
        rocksDb.Dispose();
        lmdbDatabase.Dispose();
        lmdbTransaction.Dispose();
        lmdbEnvironment.Dispose();
        database.Dispose();

        try
        {
            directory.Delete(true);
        }
        catch (DirectoryNotFoundException) { }
    }

    protected virtual void OnSetup() { }
    protected virtual void OnCleanup() { }
}
