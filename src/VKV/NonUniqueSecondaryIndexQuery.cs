using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using VKV.BTree;
using VKV.Internal;

namespace VKV;

public class NonUniqueSecondaryIndexQuery : IKeyValueStore
{
    public IKeyEncoding KeyEncoding { get; }

    readonly TreeWalker duplicateKeyTree;

    internal NonUniqueSecondaryIndexQuery(IndexDescriptor descriptor, PageCache pageCache)
    {
        KeyEncoding = descriptor.KeyEncoding;

        duplicateKeyTree = new TreeWalker(
            descriptor.RootPageNumber,
            pageCache,
            new DuplicateKeyEncoding(descriptor.KeyEncoding));
    }

    public SingleValueResult Get(ReadOnlySpan<byte> key)
    {
        Span<byte> minKeyBuffer = stackalloc byte[DuplicateKey.SizeOf(key.Length)];
        DuplicateKey.TryEncode(key, 0, minKeyBuffer);
        return duplicateKeyTree.Get(minKeyBuffer);
    }

    public async ValueTask<SingleValueResult> GetAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        var minKeyBuffer = ArrayPool<byte>.Shared.Rent(DuplicateKey.SizeOf(key.Length));
        DuplicateKey.TryEncode(key.Span, 0, minKeyBuffer);
        try
        {
            return await duplicateKeyTree.GetAsync(minKeyBuffer, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(minKeyBuffer);
        }
    }

    public RangeResult GetRange(
        ReadOnlySpan<byte> startKey,
        ReadOnlySpan<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        SortOrder sortOrder = SortOrder.Ascending)
    {
        Span<byte> minKeyBuffer = stackalloc byte[DuplicateKey.SizeOf(startKey.Length)];
        Span<byte> maxKeyBuffer = stackalloc byte[DuplicateKey.SizeOf(endKey.Length)];

        if (!startKey.IsEmpty)
        {
            DuplicateKey.TryEncode(startKey, 0, minKeyBuffer);
        }
        if (!endKey.IsEmpty)
        {
            DuplicateKey.TryEncode(endKey, int.MaxValue, maxKeyBuffer);
        }

        using var valueRefs = duplicateKeyTree.GetRange(
            minKeyBuffer,
            maxKeyBuffer,
            startKeyExclusive,
            endKeyExclusive,
            sortOrder);
        if (valueRefs.Count <= 0)
        {
            return RangeResult.Empty;
        }

        var result = RangeResult.Rent();
        foreach (var x in valueRefs)
        {
            var pageRef = PageRef.Parse(x.Span);
            IPageEntry page;
            while (!duplicateKeyTree.PageCache.TryGet(pageRef.PageNumber, out page))
            {
                duplicateKeyTree.PageCache.Load(pageRef.PageNumber);
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
        bool endKeyExclusive = false, SortOrder sortOrder = SortOrder.Ascending,
        CancellationToken cancellationToken = default)
    {
        var minKeyLength = DuplicateKey.SizeOf(startKey.Length);
        var maxKeyLength = DuplicateKey.SizeOf(endKey.Length);
        var minKeyBuffer = ArrayPool<byte>.Shared.Rent(minKeyLength);
        var maxKeyBuffer = ArrayPool<byte>.Shared.Rent(maxKeyLength);

        if (!startKey.IsEmpty)
        {
            DuplicateKey.TryEncode(startKey.Span, 0, minKeyBuffer);
        }
        if (!endKey.IsEmpty)
        {
            DuplicateKey.TryEncode(endKey.Span, int.MaxValue, maxKeyBuffer);
        }

        RangeResult valueRefs;
        try
        {
            valueRefs = await duplicateKeyTree.GetRangeAsync(
                minKeyBuffer.AsMemory(0, minKeyLength),
                maxKeyBuffer.AsMemory(0, maxKeyLength),
                startKeyExclusive,
                endKeyExclusive,
                sortOrder,
                cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(minKeyBuffer);
            ArrayPool<byte>.Shared.Return(maxKeyBuffer);
        }
        if (valueRefs.Count <= 0)
        {
            return RangeResult.Empty;
        }

        var result = RangeResult.Rent();
        using var _ = valueRefs;
        foreach (var x in valueRefs)
        {
            var pageRef = PageRef.Parse(x.Span);
            IPageEntry page;
            while (!duplicateKeyTree.PageCache.TryGet(pageRef.PageNumber, out page))
            {
                await duplicateKeyTree.PageCache.LoadAsync(pageRef.PageNumber, cancellationToken);
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
        bool endKeyExclusive = false)
    {
        Span<byte> minKeyBuffer = stackalloc byte[DuplicateKey.SizeOf(startKey.Length)];
        Span<byte> maxKeyBuffer = stackalloc byte[DuplicateKey.SizeOf(endKey.Length)];

        if (!startKey.IsEmpty)
        {
            DuplicateKey.TryEncode(startKey, 0, minKeyBuffer);
        }
        if (!endKey.IsEmpty)
        {
            DuplicateKey.TryEncode(endKey, int.MaxValue, maxKeyBuffer);
        }

        return duplicateKeyTree.CountRange(
            minKeyBuffer,
            maxKeyBuffer,
            startKeyExclusive,
            endKeyExclusive);
    }

    public async ValueTask<int> CountRangeAsync(
        ReadOnlyMemory<byte> startKey,
        ReadOnlyMemory<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        CancellationToken cancellationToken = default)
    {
        var startKeyLength = DuplicateKey.SizeOf(startKey.Length);
        var endKeyLength = DuplicateKey.SizeOf(endKey.Length);
        var minKeyBuffer = ArrayPool<byte>.Shared.Rent(startKeyLength);
        var maxKeyBuffer = ArrayPool<byte>.Shared.Rent(endKeyLength);

        if (!startKey.IsEmpty)
        {
            DuplicateKey.TryEncode(startKey.Span, 0, minKeyBuffer);
        }
        if (!endKey.IsEmpty)
        {
            DuplicateKey.TryEncode(endKey.Span, int.MaxValue, maxKeyBuffer);
        }

        try
        {
            return await duplicateKeyTree.CountRangeAsync(
                minKeyBuffer.AsMemory(0, startKeyLength),
                maxKeyBuffer.AsMemory(0, endKeyLength),
                startKeyExclusive,
                endKeyExclusive, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(minKeyBuffer);
            ArrayPool<byte>.Shared.Return(maxKeyBuffer);
        }
    }
}