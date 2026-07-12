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
        return new ReadOnlyDatabase(catalog, storage, options, stream.Length);
    }

    public Catalog Catalog { get; }
    readonly IPageLoader pageLoader;
    readonly PageCache pageCache;
    readonly Dictionary<string, ReadOnlyTable> tables;

    // A page occupies at least this many bytes on disk (headers + one entry), which
    // bounds how many pages a file can possibly contain.
    const int MinPageSizeOnDisk = 32;

    ReadOnlyDatabase(Catalog catalog, IPageLoader pageLoader, DatabaseLoadOptions options, long dataLength)
    {
        Catalog = catalog;
        this.pageLoader = pageLoader;

        // The cache can never hold more pages than the file contains: clamping the
        // capacity keeps the eviction queues and ghost table from preallocating
        // megabytes when a small database is opened with the (large) default CacheSize.
        var maxPageCount = (int)Math.Min(int.MaxValue, dataLength / MinPageSizeOnDisk + 8);
        var pageCacheCapacity = Math.Max(Math.Min(options.CacheSize / catalog.PageSize, maxPageCount), 8);
        pageCache = new PageCache(pageLoader, pageCacheCapacity, catalog.Filters?.ToArray() ?? []);

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
