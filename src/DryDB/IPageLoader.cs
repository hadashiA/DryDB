using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DryDB.BTree;

namespace DryDB;

public readonly record struct PageNumber(long Value)
{
    public static PageNumber Empty => new(-1);

    public bool IsEmpty => Value == -1;
}

public interface IPageLoader : IDisposable
{
    ValueTask<IMemoryOwner<byte>> ReadPageAsync(
        PageNumber pageNumber,
        IPageFilter[]? filters = null,
        CancellationToken cancellationToken = default);

    IMemoryOwner<byte> ReadPage(
        PageNumber pageNumber,
        IPageFilter[]? filters = null);
}

public static class PageLoaderExtensions
{
    public static int TotalPageHeaderSize => Unsafe.SizeOf<PageHeader>() +
                                             Unsafe.SizeOf<NodeHeader>();

    public static int GetTotalPageHeaderSize(this IPageLoader loader) =>
        TotalPageHeaderSize;
}