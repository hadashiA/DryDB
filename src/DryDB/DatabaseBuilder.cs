using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DryDB.BTree;
using DryDB.Internal;
#if NET7_0_OR_GREATER
using static System.Runtime.InteropServices.MemoryMarshal;
#else
using static System.Runtime.CompilerServices.MemoryMarshalEx;
#endif

namespace DryDB;

public delegate ReadOnlyMemory<byte> SecondaryIndexFactory(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value);
public delegate T SecondaryIndexFactory<out T>(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value)
    where T : IComparable<T>;

public abstract class IndexOptions(string name, bool isUnique)
{
    public string Name => name;
    public bool IsUnique => isUnique;

    public IKeyEncoding KeyEncoding { get; set; } = DryDB.KeyEncoding.Ascii;
    public abstract ValueKind ValueKind { get; }
}

public class PrimaryKeyIndexOptions(string name) : IndexOptions(name, true)
{
    public override ValueKind ValueKind => ValueKind.RawData;
}

public class SecondaryIndexOptions(string name, bool isUnique) : IndexOptions(name, isUnique)
{
    public override ValueKind ValueKind => ValueKind.PageRef;
    public required SecondaryIndexFactory IndexFactory;
}

public class FilterOptions
{
    public IReadOnlyList<IPageFilter> Filters => pageFilters;

    readonly List<IPageFilter> pageFilters = [];

    public void AddFilter(IPageFilter filter)
    {
        pageFilters.Add(filter);
    }
}

public class TableOptions
{
    public required string Name { get; set; }
    public required PrimaryKeyIndexOptions PrimaryKeyIndexOptions { get; set; }
    public List<SecondaryIndexOptions> SecondaryIndexOptionsList { get; set; } = [];
}

public class TableBuilder
{
    public string Name { get; set; }
    public IKeyEncoding PrimaryKeyEncoding { get; set; }
    public IReadOnlyList<SecondaryIndexOptions> SecondaryIndexOptions => secondaryIndexOptions;
    internal KeyValueList KeyValues => keyValues;

    readonly List<SecondaryIndexOptions> secondaryIndexOptions = [];
    readonly KeyValueList keyValues;

    internal TableBuilder(string name, IKeyEncoding primaryKeyEncoding)
    {
        Name = name;
        PrimaryKeyEncoding = primaryKeyEncoding;
        keyValues = KeyValueList.Create(PrimaryKeyEncoding, true);
    }

    public void AddSecondaryIndex(
        string indexName,
        bool isUnique,
        IKeyEncoding keyEncoding,
        SecondaryIndexFactory indexFactory)
    {
        secondaryIndexOptions.Add(new SecondaryIndexOptions(indexName, isUnique)
        {
            KeyEncoding = keyEncoding,
            IndexFactory = indexFactory,
        });
    }

    public void AddSecondaryIndex<TIndex>(
        string indexName,
        bool isUnique,
        IKeyEncoding keyEncoding,
        SecondaryIndexFactory<TIndex> indexFactory)
        where TIndex : IComparable<TIndex>
    {
        SecondaryIndexFactory factory = (key, value) =>
        {
            var typedIndex = indexFactory(key, value);
            var length = keyEncoding.GetMaxEncodedByteCount(typedIndex);
            var buffer = new byte[length];
            keyEncoding.TryEncode(typedIndex, buffer, out var written);
            return buffer.AsMemory(0, written);
        };
        AddSecondaryIndex(indexName, isUnique, keyEncoding, factory);
    }

    public void Append(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value)
    {
        keyValues.Add(key, value);
    }

    public void Append<TKey>(TKey key, ReadOnlyMemory<byte> value) where TKey : IComparable<TKey>
    {
        keyValues.Add(key, value);
    }

    public TableOptions ToTableOptions()
    {
        return new TableOptions
        {
            Name = Name,
            PrimaryKeyIndexOptions = new PrimaryKeyIndexOptions($"{Name}_pk")
            {
                KeyEncoding = PrimaryKeyEncoding,
            },
            SecondaryIndexOptionsList = secondaryIndexOptions
        };
    }
}

public class DatabaseBuilder : IDisposable
{
    public int PageSize { get; set; } = 4096;

    /// <summary>
    /// Store an order-preserving 8-byte digest per entry in every B+Tree node, which
    /// speeds up key searches (~20-30% for cache-resident reads) at the cost of 8 bytes
    /// per entry of file size. Encodings without digest support (e.g. UUIDv7 or custom
    /// encodings) always use the plain layout regardless of this setting.
    /// Disable to produce files byte-compatible with DryDB 1.0 readers.
    /// </summary>
    public bool KeyDigests { get; set; } = true;

    /// <summary>
    /// Store each node's key digest array as a MaxValue-padded complete binary tree in
    /// Eytzinger (BFS) order instead of sorted order, which makes the digest search a
    /// branch-free descent whose top levels share a cache line. Costs up to 2x the
    /// digest area (padding to 2^k - 1 slots) and produces format 1.2 files, which
    /// readers older than 1.2 cannot parse. No effect unless <see cref="KeyDigests"/>
    /// is enabled and the encoding supports digests.
    /// </summary>
    public bool EytzingerDigests { get; set; } = false;

    readonly MemoryArena arena = new();
    readonly List<TableBuilder> tableBuilders = [];
    FilterOptions? filterOptions;

    public void AddPageFilter(Action<FilterOptions> configure)
    {
        filterOptions ??= new FilterOptions();
        configure.Invoke(filterOptions);
    }

    public TableBuilder CreateTable(string name)
    {
        return CreateTable(name, AsciiOrdinalEncoding.Instance);
    }

    public TableBuilder CreateTable(string name, IKeyEncoding primaryKeyEncoding)
    {
        var tableBuilder = new TableBuilder(name, primaryKeyEncoding);
        tableBuilders.Add(tableBuilder);
        return tableBuilder;
    }

    public async ValueTask BuildToFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var fs = File.OpenWrite(path);
        await BuildToStreamAsync(fs, cancellationToken);
    }

    public async ValueTask BuildToStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new Header();

        unsafe
        {
            Header.MagicBytesValue.CopyTo(new Span<byte>(header.MagicBytes, Header.MagicBytesValue.Length));
        }
        header.MajorVersion = 1;
        // 1.1: B+Tree nodes may carry per-entry key digest arrays (flagged per page in
        // the node header's kind field). 1.0 readers cannot parse such pages, so files
        // built with KeyDigests disabled stay marked (and byte-compatible) as 1.0.
        // 1.2: the digest array may be Eytzinger-ordered (a second per-page flag);
        // 1.1 readers cannot parse such pages.
        header.MinorVersion = (byte)(KeyDigests ? (EytzingerDigests ? 2 : 1) : 0);
        header.PageFilterCount = (ushort)(filterOptions?.Filters.Count ?? 0);
        header.PageSize = PageSize;
        header.TableCount = (ushort)tableBuilders.Count;

        await DryDBCodec.WriteDatabaseHeaderAsync(stream, header, filterOptions, cancellationToken);

        var tableOptions = new TableOptions[tableBuilders.Count];
        var indexDescriptorEndPositionsList = new List<long[]>();

        for (var i = 0; i < tableBuilders.Count; i++)
        {
            tableOptions[i] = tableBuilders[i].ToTableOptions();
        }

        for (var i = 0; i < tableBuilders.Count; i++)
        {
            var positions = await DryDBCodec.WriteTableDescriptorAsync(
                stream,
                tableOptions[i],
                cancellationToken);
            indexDescriptorEndPositionsList.Add(positions);
        }

        for (var i = 0; i < tableBuilders.Count; i++)
        {
            await DryDBCodec.BuildTreeAsync(
                stream,
                PageSize,
                tableOptions[i],
                tableBuilders[i].KeyValues,
                filterOptions?.Filters,
                indexDescriptorEndPositionsList[i],
                KeyDigests,
                EytzingerDigests,
                cancellationToken);
        }

        await stream.FlushAsync(cancellationToken);
    }

    public void Dispose()
    {
        arena.Dispose();
    }
}
