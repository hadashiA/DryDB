using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DryDB.BTree;
using DryDB.Internal;

namespace DryDB;

public sealed class ReadOnlyTable : IKeyValueStore
{
    public string Name => descriptor.Name;
    public IKeyEncoding KeyEncoding { get; }

    readonly TableDescriptor descriptor;
    readonly TreeWalker primaryKeyTree;
    readonly Dictionary<string, IKeyValueStore> secondaryIndexQueries;

    internal ReadOnlyTable(TableDescriptor descriptor, PageCache pageCache)
    {
        this.descriptor = descriptor;
        KeyEncoding = descriptor.PrimaryKeyDescriptor.KeyEncoding;

        primaryKeyTree = new TreeWalker(
            descriptor.PrimaryKeyDescriptor.RootPageNumber,
            pageCache,
            descriptor.PrimaryKeyDescriptor.KeyEncoding);

        secondaryIndexQueries = new Dictionary<string, IKeyValueStore>(descriptor.IndexDescriptors.Count);
        foreach (var indexDescriptor in descriptor.IndexDescriptors)
        {
            IKeyValueStore query = indexDescriptor.IsUnique
                ? new SecondaryIndexQuery(indexDescriptor, pageCache)
                : new NonUniqueSecondaryIndexQuery(indexDescriptor, pageCache);

            secondaryIndexQueries.Add(indexDescriptor.Name, query);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SingleValueResult Get(ReadOnlySpan<byte> key) => primaryKeyTree.Get(key);

    // this overload is optimize purpose
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SingleValueResult Get(long key)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(buffer), key);
        return primaryKeyTree.Get(buffer);
    }

    // this overload is optimize purpose
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SingleValueResult Get(int key) => Get((long)key);

    // this overload is optimize purpose
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SingleValueResult Get<TKey>(TKey key)
        where TKey : IComparable<TKey>
    {
        var bufferLength = KeyEncoding.GetMaxEncodedByteCount(key);
        Span<byte> buffer = stackalloc byte[bufferLength];
        KeyEncoding.TryEncode(key, buffer, out var bytesWritten);
        return Get(buffer[..bytesWritten]);
    }

    /// <summary>
    /// Find value by key.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="cancellationToken"></param>
    /// <returns>
    ///   return byte array if found, otherwise null.
    /// </returns>
    public ValueTask<SingleValueResult> GetAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) =>
        primaryKeyTree.GetAsync(key, cancellationToken);

    // optimization for long
    public async ValueTask<SingleValueResult> GetAsync(long key, CancellationToken cancellationToken = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(buffer, key);
        try
        {
            return await primaryKeyTree.GetAsync(buffer.AsMemory(0, sizeof(long)), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask<SingleValueResult> GetAsync<TKey>(TKey key, CancellationToken cancellationToken = default)
        where TKey : IComparable<TKey>
    {
        var initialBufferSize = KeyEncoding.GetMaxEncodedByteCount(key);
        var buffer = ArrayPool<byte>.Shared.Rent(initialBufferSize);
        int bytesWritten;
        while (!KeyEncoding.TryEncode(key, buffer, out bytesWritten))
        {
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
        }
        try
        {
            return await primaryKeyTree.GetAsync(buffer.AsMemory(0, bytesWritten), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public RangeResult GetRange(
        ReadOnlySpan<byte> startKey,
        ReadOnlySpan<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        SortOrder sortOrder = SortOrder.Ascending) =>
        primaryKeyTree.GetRange(startKey, endKey, startKeyExclusive, endKeyExclusive, sortOrder);

    public ValueTask<RangeResult> GetRangeAsync(
        ReadOnlyMemory<byte> startKey,
        ReadOnlyMemory<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        SortOrder sortOrder = SortOrder.Ascending,
        CancellationToken cancellationToken = default) =>
        primaryKeyTree.GetRangeAsync(startKey, endKey, startKeyExclusive, endKeyExclusive, sortOrder, cancellationToken);

    public ValueTask<RangeResult> GetRangeAsync<TKey>(
        ReadOnlyMemory<byte> startKey,
        ReadOnlyMemory<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        SortOrder sortOrder = SortOrder.Ascending,
        CancellationToken cancellationToken = default)
        where TKey : IComparable<TKey> =>
        primaryKeyTree.GetRangeAsync(startKey, endKey, startKeyExclusive, endKeyExclusive, sortOrder, cancellationToken);

    public int CountRange(
        ReadOnlySpan<byte> startKey,
        ReadOnlySpan<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false) =>
        primaryKeyTree.CountRange(startKey, endKey, startKeyExclusive, endKeyExclusive);

    public ValueTask<int> CountRangeAsync(
        ReadOnlyMemory<byte> startKey,
        ReadOnlyMemory<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        CancellationToken cancellationToken = default) =>
        primaryKeyTree.CountRangeAsync(startKey, endKey, startKeyExclusive, endKeyExclusive, cancellationToken);

    public RangeIterator CreateIterator(IteratorDirection iteratorDirection = IteratorDirection.Forward) =>
        new(primaryKeyTree, iteratorDirection);

    public IKeyValueStore Index(string indexName) => secondaryIndexQueries[indexName];
}
