using System;
using System.IO;
using System.Threading.Tasks;

namespace DryDB.Tests;

[TestFixture]
public class ReadOnlyDatabaseTest
{
    [Test]
    public async Task OpenFromMemory()
    {
        var bytes = await BuildDatabaseBytesAsync();

        using var database = await ReadOnlyDatabase.OpenAsync((ReadOnlyMemory<byte>)bytes);
        var table = database.GetTable("items");

        var result = await table.GetAsync("item2"u8.ToArray());
        Assert.That(result.Value.Span.SequenceEqual("value2"u8), Is.True);
    }

    [Test]
    public async Task OpenFromExposableMemoryStream()
    {
        var memoryStream = new MemoryStream();
        var builder = CreateBuilder();
        await builder.BuildToStreamAsync(memoryStream);
        memoryStream.Seek(0, SeekOrigin.Begin);

        // TryGetBuffer succeeds here: the loader reads the stream's own buffer.
        using var database = await ReadOnlyDatabase.OpenAsync(memoryStream);
        var table = database.GetTable("items");

        var result = await table.GetAsync("item1"u8.ToArray());
        Assert.That(result.Value.Span.SequenceEqual("value1"u8), Is.True);
    }

    [Test]
    public async Task OpenFromNonExposableMemoryStream()
    {
        var bytes = await BuildDatabaseBytesAsync();

        // A byte[]-wrapping MemoryStream does not expose its buffer;
        // this exercises the ToArray fallback.
        using var database = await ReadOnlyDatabase.OpenAsync(new MemoryStream(bytes));
        var table = database.GetTable("items");

        var result = await table.GetAsync("item3"u8.ToArray());
        Assert.That(result.Value.Span.SequenceEqual("value3"u8), Is.True);
    }

    static async ValueTask<byte[]> BuildDatabaseBytesAsync()
    {
        var memoryStream = new MemoryStream();
        var builder = CreateBuilder();
        await builder.BuildToStreamAsync(memoryStream);
        return memoryStream.ToArray();
    }

    static DatabaseBuilder CreateBuilder()
    {
        var builder = new DatabaseBuilder();
        var itemsTable = builder.CreateTable("items");
        itemsTable.Append("item1"u8.ToArray(), "value1"u8.ToArray());
        itemsTable.Append("item2"u8.ToArray(), "value2"u8.ToArray());
        itemsTable.Append("item3"u8.ToArray(), "value3"u8.ToArray());
        return builder;
    }
}
