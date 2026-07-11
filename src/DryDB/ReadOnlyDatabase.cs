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

public enum PageReclamation
{
    /// <summary>
    /// <see cref="Gc"/> on CoreCLR; <see cref="ReferenceCounted"/> on Mono/IL2CPP (Unity),
    /// where the conservative non-compacting GC makes per-load garbage expensive.
    /// </summary>
    Auto,

    /// <summary>
    /// Page buffers are reference counted and returned to the buffer pool deterministically.
    /// Steady-state loads allocate nothing, at the cost of interlocked refcount traffic on
    /// every page access (a contention point for parallel reads of hot pages).
    /// </summary>
    ReferenceCounted,

    /// <summary>
    /// Evicted page buffers are left to the garbage collector; reads perform no interlocked
    /// operations at all. Best multi-thread read throughput; each page load allocates.
    /// </summary>
    Gc,
}

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

    /// <summary>
    /// How managed page buffers are reclaimed after eviction. Buffers over unmanaged
    /// memory (e.g. the Unity NativeArray loader) are always reference counted
    /// regardless of this setting, because only a refcount can release them safely.
    /// </summary>
    public PageReclamation PageReclamation { get; set; } = PageReclamation.Auto;

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

    // Mono (Unity editor/player) and IL2CPP use a conservative, non-compacting GC where
    // per-load garbage hurts both peak memory and collection pauses — prefer deterministic
    // pooling there. CoreCLR handles short-lived buffers cheaply.
    static readonly bool GcReclamationByDefault =
        Type.GetType("Mono.Runtime") == null &&
        System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;

    public Catalog Catalog { get; }
    readonly IPageLoader pageLoader;
    readonly PageCache pageCache;
    readonly Dictionary<string, ReadOnlyTable> tables;

    ReadOnlyDatabase(Catalog catalog, IPageLoader pageLoader, DatabaseLoadOptions options)
    {
        Catalog = catalog;
        this.pageLoader = pageLoader;
        var pageCacheCapacity = Math.Max(options.CacheSize / catalog.PageSize, 8);
        var gcReclamation = options.PageReclamation switch
        {
            PageReclamation.ReferenceCounted => false,
            PageReclamation.Gc => true,
            _ => GcReclamationByDefault,
        };
        pageCache = new PageCache(
            pageLoader,
            pageCacheCapacity,
            catalog.Filters?.ToArray() ?? [],
            gcReclamation: gcReclamation);

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
