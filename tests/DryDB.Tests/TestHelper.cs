using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DryDB.BTree;
using DryDB.Internal;
using DryDB.Storages;

namespace DryDB.Tests;

static class TestHelper
{
    public static async ValueTask<TreeWalker> BuildTreeAsync(KeyValueList keyValues, int pageSize)
    {
        var memoryStream = new MemoryStream();
        var buildResult = await TreeBuilder.BuildToAsync(
            memoryStream,
            pageSize,
            keyValues);

        var result = memoryStream.ToArray();
        var storage = new InMemoryPageLoader(result.ToArray());
        var pageCache = new PageCache(storage, 8, []);
        return new TreeWalker(buildResult.RootPageNumber, pageCache, KeyEncoding.Ascii);
    }

    public static async ValueTask<ReadOnlyTable> BuildTableAsync(
        IKeyEncoding keyEncoding,
        DatabaseLoadOptions? loadOptions = null,
        Action<DatabaseBuilder>? databaseConfigure = null,
        Action<TableBuilder>? tableConfigure = null)
    {
        var builder = new DatabaseBuilder();
        databaseConfigure?.Invoke(builder);

        var tableBuilder = builder.CreateTable("items", keyEncoding);
        tableConfigure?.Invoke(tableBuilder);

        var memoryStream = new MemoryStream(1024);
        await builder.BuildToStreamAsync(memoryStream);

        memoryStream.Seek(0, SeekOrigin.Begin);
        var database = await ReadOnlyDatabase.OpenAsync(memoryStream, loadOptions);
        return database.GetTable("items");
    }


    static void AddUtf8KeyValues(TableBuilder tableBuilder, int size)
    {
        for (var i = 0; i < size; i++)
        {
            var key = $"key{i}";
            var value = $"value{i}";

            tableBuilder.Append(
                Encoding.UTF8.GetBytes(key),
                Encoding.UTF8.GetBytes(value));
        }
    }
}