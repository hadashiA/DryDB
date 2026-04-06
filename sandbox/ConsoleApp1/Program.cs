using System.Text;
// using CsSqlite;
using DryDB;

var directory = Directory.CreateTempSubdirectory("drydb_benchmarks");
// var sqlitePath = Path.Combine(directory.FullName, "bench.sqlite");
var filePath = Path.Combine(directory.FullName, "bench.drydb");

// Setup DryDB
using (var builder = new DatabaseBuilder
       {
           PageSize = 4096
       })
{
    var tableBuilder = builder.CreateTable("table1", KeyEncoding.Ascii);
    for (var i = 0; i < 1000; i++)
    {
        tableBuilder.Append($"key{i:D4}", Encoding.UTF8.GetBytes($"value{i:D4}"));
    }

    var tableBuilder2 = builder.CreateTable("table2", KeyEncoding.Int64LittleEndian);
    for (var i = 0; i < 1000; i++)
    {
        tableBuilder2.Append(i, Encoding.UTF8.GetBytes($"value{i:D4}"));
    }

    var tableBuilder3 = builder.CreateTable("table3", KeyEncoding.Uuidv7);
    for (var i = 0; i < 1000; i++)
    {
        tableBuilder3.Append(Guid.CreateVersion7(), Encoding.UTF8.GetBytes($"value{i:D4}"));
    }


    await builder.BuildToFileAsync(filePath);
    // var memoryStream = new MemoryStream();
    // await builder.SaveToStreamAsync(memoryStream);
    Console.WriteLine(filePath);
}

var bytes = File.ReadAllBytes(filePath);

//using (var database = await ReadOnlyDatabase.OpenFileAsync(filePath))
using (var database = await ReadOnlyDatabase.OpenAsync(new MemoryStream(bytes)))
{
    var table = database.GetTable("table2");

    while (true)
    {

        using var result = table.Get(123);
        // Console.WriteLine(Encoding.ASCII.GetString(result.Span));
    }
}

// using (var sqlite = new SqliteConnection(sqlitePath))
// {
//     sqlite.Open();
//
//     sqlite.ExecuteNonQuery("DROP TABLE IF EXISTS items;");
//
//     sqlite.ExecuteNonQuery("PRAGMA page_size = 4096;");
//
//     sqlite.ExecuteNonQuery(
//         """
//         CREATE TABLE IF NOT EXISTS items (
//             id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
//             data TEXT NOT NULL
//         );
//         """);
//
//     for (var i = 0; i < 1000; i++)
//     {
//         sqlite.ExecuteNonQuery(
//             $"""
//              INSERT INTO items (id, data) VALUES ({i}, '{i:D10}');
//              """);
//     }
// }
//
// using (var sqlite = new SqliteConnection(sqlitePath))
// {
//     using var command = sqlite.CreateCommand(
//         "SELECT data FROM items WHERE id = $id");
//
//     command.Parameters.Add("$id", 123);
//     using var reader = command.ExecuteReader();
//     reader.Read();
//     var result = reader.GetString(0);
//     Console.WriteLine(result);
// }
//
// Console.WriteLine($"{sqlitePath}");
