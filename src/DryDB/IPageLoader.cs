using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DryDB.BTree;

namespace DryDB;

/// <summary>
/// Identifies a page by its dense ordinal (0..pageCount-1, assigned in flush order
/// at build time). The page directory in the catalog maps an ordinal to its byte
/// offset in the file; the page cache is indexed directly by the ordinal.
/// </summary>
public readonly record struct PageNumber(long Value)
{
    public static PageNumber Empty => new(-1);

    public bool IsEmpty => Value == -1;
}

/// <summary>
/// Reads one page's raw bytes. <paramref name="position"/> is the byte offset of the
/// page in the file (already translated from the page ordinal via the catalog's page
/// directory); the first 4 bytes at that position are the page length.
/// </summary>
public interface IPageLoader : IDisposable
{
    ValueTask<IMemoryOwner<byte>> ReadPageAsync(
        long position,
        IPageFilter[]? filters = null,
        CancellationToken cancellationToken = default);

    IMemoryOwner<byte> ReadPage(
        long position,
        IPageFilter[]? filters = null);
}

public static class PageLoaderExtensions
{
    public static int TotalPageHeaderSize => Unsafe.SizeOf<PageHeader>() +
                                             Unsafe.SizeOf<NodeHeader>();

    public static int GetTotalPageHeaderSize(this IPageLoader loader) =>
        TotalPageHeaderSize;
}