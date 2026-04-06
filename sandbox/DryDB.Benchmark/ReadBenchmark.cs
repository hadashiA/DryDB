using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using CsSqlite;

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

[Config(typeof(BenchmarkConfig))]
public class ReadBenchmark
{
    const int N = 10000;

    DirectoryInfo directory;
    ReadOnlyDatabase database;
    SqliteConnection cssqliteConnection;
    System.Data.SQLite.SQLiteConnection systemSqliteConnection;

    string findKey = "key0000001234";

    [GlobalSetup]
    public async Task CreateDB()
    {
        directory = Directory.CreateTempSubdirectory("drydb_benchmarks");
        var sqlitePath = Path.Combine(directory.FullName, "bench.sqlite");
        var drydbPath = Path.Combine(directory.FullName, "bench.drydb");

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

            for (var i = 0; i < N; i++)
            {
                sqlite.ExecuteNonQuery(
                    $"""
                     INSERT INTO items (id, data) VALUES ({i}, 'val{i:D10}');
                     """);
            }
        }

        // Setup  DryDB
        using var builder = new DatabaseBuilder
        {
            PageSize = 4096,
        };
        var tableBuilder = builder.CreateTable("items", KeyEncoding.Int64LittleEndian);
        for (var i = 0; i < N; i++)
        {
            tableBuilder.Append(i, Encoding.UTF8.GetBytes($"val{i:D10}"));
        }
        await builder.BuildToFileAsync(drydbPath);

        database = await ReadOnlyDatabase.OpenFileAsync(drydbPath, new DatabaseLoadOptions
        {
        });

        cssqliteConnection = new SqliteConnection(sqlitePath);
        // systemSqliteConnection = new System.Data.SQLite.SQLiteConnection($"Data Source={sqlitePath}");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        cssqliteConnection.Dispose();
        // systemSqliteConnection.Dispose();
        database.Dispose();

        try
        {
            directory.Delete(true);
        }
        catch (DirectoryNotFoundException) { }
    }

    [Benchmark(Baseline = true)]
    public void DryDB_FindByKey()
    {
        for (var i = 0; i < 1000; i++)
        {
            var table = database.GetTable("items");
            using var _ = table.Get(123);
        }
    }

    [Benchmark]
    public void CsSqlite_FindByKey()
    {
        for (var i = 0; i < 1000; i++)
        {
            using var command = cssqliteConnection.CreateCommand(
                "SELECT data FROM items WHERE id = $id");

            command.Parameters.Add("$id", 123);
            using var reader = command.ExecuteReader();
            reader.Read();
            reader.GetString(0);
        }
    }
    //
    // [Benchmark]
    // public void SystemDataSql_FindByKey()
    // {
    //     for (var i = 0; i < 1000; i++)
    //     {
    //         using var command = systemSqliteConnection.CreateCommand();
    //         command.CommandText = "SELECT data FROM items WHERE id = $id";
    //         command.Parameters.AddWithValue("$id", 123);
    //         using var reader = command.ExecuteReader();
    //         reader.Read();
    //         reader.GetString(0);
    //     }
    // }
}
