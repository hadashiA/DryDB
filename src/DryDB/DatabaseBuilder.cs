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
        header.MinorVersion = 0;
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
                cancellationToken);
        }

        await stream.FlushAsync(cancellationToken);
    }

    public void Dispose()
    {
        arena.Dispose();
    }
}
