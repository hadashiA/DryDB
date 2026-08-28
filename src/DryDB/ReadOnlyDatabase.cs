using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DryDB.Internal;
using DryDB.Storages;

namespace DryDB;

public delegate IPageLoader StorageFactory(Stream stream, int pageSize);

public record DatabaseLoadOptions
{
    public static DatabaseLoadOptions Default => new();

    public static readonly StorageFactory DefaultStorageFactory = (stream, pageSize) =>
    {
        if (stream is FileStream fs)
        {
            return new PreadPageLoader(fs.SafeFileHandle);
        }

        if (stream is MemoryStream ms)
        {
            return new InMemoryPageLoader(ms.ToArray());
        }

        throw new NotSupportedException($"unsupported stream type: {stream.GetType().Name}");
    };

    public int CacheSize { get; set; } = 2000 * 1024 * 1024;
    public StorageFactory StorageFactory { get; set; } = DefaultStorageFactory;
}

public sealed class ReadOnlyDatabase : IDisposable
{
    public static async ValueTask<ReadOnlyDatabase> OpenFileAsync(string path, DatabaseLoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= DatabaseLoadOptions.Default;
        var fs = File.OpenRead(path);
        return await OpenAsync(fs, options, cancellationToken);
    }

    public static async ValueTask<ReadOnlyDatabase> OpenAsync(Stream stream, DatabaseLoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= DatabaseLoadOptions.Default;
        var catalog = await DryDBCodec.ParseCatalogAsync(stream, cancellationToken);
        var storage = options.StorageFactory.Invoke(stream, catalog.PageSize);
        return new ReadOnlyDatabase(catalog, storage, options);
    }

    public Catalog Catalog { get; }
    readonly IPageLoader pageLoader;
    readonly PageCache pageCache;
    readonly Dictionary<string, ReadOnlyTable> tables;

    ReadOnlyDatabase(Catalog catalog, IPageLoader pageLoader, DatabaseLoadOptions options)
    {
        Catalog = catalog;
        this.pageLoader = pageLoader;

        // The exact page count comes from the page directory; the cache never needs
        // to hold more pages than the file contains.
        var pageCacheCapacity = Math.Max(Math.Min(options.CacheSize / catalog.PageSize, catalog.PageOffsets.Length), 8);
        pageCache = new PageCache(pageLoader, catalog.PageOffsets, pageCacheCapacity, catalog.Filters?.ToArray() ?? []);

        tables = new Dictionary<string, ReadOnlyTable>(catalog.TableDescriptors.Count);
        foreach (var descriptor in catalog.TableDescriptors.Values)
        {
            tables.Add(descriptor.Name, new ReadOnlyTable(descriptor, pageCache));
        }
    }

    public void Dispose()
    {
        foreach (var table in tables.Values)
        {
            table.ReleasePinnedPages();
        }
        pageCache.Dispose();
        pageLoader.Dispose();
    }

    public ReadOnlyTable GetTable(string name) => tables[name];
}
