using System.Collections.Generic;

namespace DryDB;

public enum ValueKind : byte
{
    RawData,
    PrimaryKey,
    PageRef,
}

public class IndexDescriptor
{
    public required string Name { get; init; }
    public required bool IsUnique { get; init; }
    public required IKeyEncoding KeyEncoding { get; init; }
    public required ValueKind ValueKind { get; init; }
    public required PageNumber RootPageNumber { get; init; }
}

public class TableDescriptor(
    string name,
    IndexDescriptor primaryKeyDescriptor,
    IReadOnlyList<IndexDescriptor> indexDescriptors)
{
    public string Name => name;
    public IndexDescriptor PrimaryKeyDescriptor => primaryKeyDescriptor;
    public IReadOnlyList<IndexDescriptor> IndexDescriptors => indexDescriptors;
}

public class Catalog(
    int pageSize,
    IReadOnlyDictionary<string, TableDescriptor> tableDescriptors,
    IReadOnlyList<IPageFilter>? filters = null)
{
    public int PageSize => pageSize;
    public IReadOnlyList<IPageFilter>? Filters => filters;
    public IReadOnlyDictionary<string, TableDescriptor> TableDescriptors => tableDescriptors;
}
