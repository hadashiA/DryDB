using System;
using System.Threading;
using System.Threading.Tasks;
using DryDB.BTree;
using DryDB.Internal;

namespace DryDB;

public class SecondaryIndexQuery : IKeyValueStore
{
    public IKeyEncoding KeyEncoding => tree.KeyEncoding;

    readonly TreeWalker tree;

    internal SecondaryIndexQuery(IndexDescriptor descriptor, PageCache pageCache)
    {
        tree = new TreeWalker(descriptor.RootPageNumber, pageCache, descriptor.KeyEncoding);
    }

    public SingleValueResult Get(ReadOnlySpan<byte> key)
    {
        using var result = tree.Get(key);
        if (!result.HasValue)
        {
            return default;
        }

        var pageRef = PageRef.Parse(result.Value.Span);

        IPageEntry page;
        while (!tree.PageCache.TryGet(pageRef.PageNumber, out page))
        {
            tree.PageCache.Load(pageRef.PageNumber);
        }

        var pageSlice = new PageSlice(page, pageRef.Start, pageRef.Length);
        return new SingleValueResult(pageSlice, true);
    }

    public async ValueTask<SingleValueResult> GetAsync(
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken = default)
    {
        using var result = await tree.GetAsync(key, cancellationToken);
        if (!result.HasValue)
        {
            return default;
        }

        var pageRef = PageRef.Parse(result.Value.Span);

        IPageEntry page;
        while (!tree.PageCache.TryGet(pageRef.PageNumber, out page))
        {
            await tree.PageCache.LoadAsync(pageRef.PageNumber, cancellationToken);
        }

        var pageSlice = new PageSlice(page, pageRef.Start, pageRef.Length);
        return new SingleValueResult(pageSlice, true);
    }

    public RangeResult GetRange(
        ReadOnlySpan<byte> startKey,
        ReadOnlySpan<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        SortOrder sortOrder = SortOrder.Ascending)
    {
        using var pageRefs = tree.GetRange(startKey, endKey, startKeyExclusive, endKeyExclusive, sortOrder);
        if (pageRefs.Count <= 0)
        {
            return RangeResult.Empty;
        }

        var result = RangeResult.Rent();
        foreach (var x in pageRefs)
        {
            var pageRef = PageRef.Parse(x.Span);
            IPageEntry page;
            while (!tree.PageCache.TryGet(pageRef.PageNumber, out page))
            {
                tree.PageCache.Load(pageRef.PageNumber);
            }
            result.Add(page, pageRef.Start, pageRef.Length);
            page.Release();
        }
        return result;
    }

    public async ValueTask<RangeResult> GetRangeAsync(
        ReadOnlyMemory<byte> startKey,
        ReadOnlyMemory<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        SortOrder sortOrder = SortOrder.Ascending,
        CancellationToken cancellationToken = default)
    {
        using var pageRefs = await tree.GetRangeAsync(
            startKey, endKey, startKeyExclusive, endKeyExclusive, sortOrder, cancellationToken);
        if (pageRefs.Count <= 0)
        {
            return [];
        }

        var result = RangeResult.Rent();
        foreach (var x in pageRefs)
        {
            var pageRef = PageRef.Parse(x.Span);
            IPageEntry page;
            while (!tree.PageCache.TryGet(pageRef.PageNumber, out page))
            {
                await tree.PageCache.LoadAsync(pageRef.PageNumber, cancellationToken);
            }
            result.Add(page, pageRef.Start, pageRef.Length);
            page.Release();
        }
        return result;
    }

    public int CountRange(
        ReadOnlySpan<byte> startKey,
        ReadOnlySpan<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false) =>
        tree.CountRange(startKey, endKey, startKeyExclusive, endKeyExclusive);

    public ValueTask<int> CountRangeAsync(
        ReadOnlyMemory<byte> startKey,
        ReadOnlyMemory<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        CancellationToken cancellationToken = default) =>
        tree.CountRangeAsync(startKey, endKey, startKeyExclusive, endKeyExclusive, cancellationToken);
}
