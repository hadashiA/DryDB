using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace VKV;

public interface IKeyValueStore
{
    IKeyEncoding KeyEncoding { get; }

    SingleValueResult Get(ReadOnlySpan<byte> key);
    ValueTask<SingleValueResult> GetAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);

    RangeResult GetRange(
        ReadOnlySpan<byte> startKey,
        ReadOnlySpan<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        SortOrder sortOrder = SortOrder.Ascending);

    ValueTask<RangeResult> GetRangeAsync(
        ReadOnlyMemory<byte> startKey,
        ReadOnlyMemory<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        SortOrder sortOrder = SortOrder.Ascending,
        CancellationToken cancellationToken = default);

    int CountRange(
        ReadOnlySpan<byte> startKey,
        ReadOnlySpan<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false);

    ValueTask<int> CountRangeAsync(
        ReadOnlyMemory<byte> startKey,
        ReadOnlyMemory<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        CancellationToken cancellationToken = default);
}

public static class KeyValueStoreExtensions
{
    public static SingleValueResult Get<TKey>(this IKeyValueStore kv, TKey key) where TKey : IComparable<TKey>
    {
        var bufferLength = kv.KeyEncoding.GetMaxEncodedByteCount(key);
        Span<byte> buffer = stackalloc byte[bufferLength];
        kv.KeyEncoding.TryEncode(key, buffer, out var bytesWritten);
        return kv.Get(buffer[..bytesWritten]);
    }

    public static async ValueTask<SingleValueResult> GetAsync<TKey>(
        this IKeyValueStore kv,
        TKey key,
        CancellationToken cancellationToken = default)
        where TKey : IComparable<TKey>
    {
        var initialBufferSize = kv.KeyEncoding.GetMaxEncodedByteCount(key);
        var buffer = ArrayPool<byte>.Shared.Rent(initialBufferSize);
        int bytesWritten;
        while (!kv.KeyEncoding.TryEncode(key, buffer, out bytesWritten))
        {
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
        }
        try
        {
            return await kv.GetAsync(buffer.AsMemory(0, bytesWritten), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static RangeResult GetRange<TKey>(
        this IKeyValueStore kv,
        TKey? startKey,
        TKey? endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        SortOrder sortOrder = SortOrder.Ascending)
        where TKey : IComparable<TKey>
    {
        var startKeyBuffer = startKey != null
            ? stackalloc byte[kv.KeyEncoding.GetMaxEncodedByteCount(startKey)]
            : [];

        var endKeyBuffer = endKey != null
            ? stackalloc byte[kv.KeyEncoding.GetMaxEncodedByteCount(endKey)]
            : [];

        var startKeyLength = 0;
        var endKeyLength = 0;

        if (startKey is { } s)
        {
            while (!kv.KeyEncoding.TryEncode(s, startKeyBuffer, out startKeyLength))
            {
                startKeyBuffer = stackalloc byte[startKeyBuffer.Length * 2];
            }
        }
        if (endKey is { } e)
        {
            while (!kv.KeyEncoding.TryEncode(e, endKeyBuffer, out endKeyLength))
            {
                endKeyBuffer = stackalloc byte[endKeyBuffer.Length * 2];
            }
        }

        return kv.GetRange(
            startKeyLength > 0 ? startKeyBuffer[..startKeyLength] : KeyRange.Unbound,
            endKeyLength > 0 ? endKeyBuffer[..endKeyLength] : KeyRange.Unbound,
            startKeyExclusive,
            endKeyExclusive,
            sortOrder);
    }

    public static async ValueTask<RangeResult> GetRangeAsync<TKey>(
        this IKeyValueStore kv,
        TKey? startKey,
        TKey? endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        SortOrder sortOrder =  SortOrder.Ascending,
        CancellationToken cancellationToken = default)
        where TKey : IComparable<TKey>
    {
        byte[]? startKeyBuffer = null;
        byte[]? endKeyBuffer = null;

        var startKeyLength = 0;
        var endKeyLength = 0;

        if (startKey != null)
        {
            var initialBufferSize = kv.KeyEncoding.GetMaxEncodedByteCount(startKey);
            startKeyBuffer = ArrayPool<byte>.Shared.Rent(initialBufferSize);
            while (!kv.KeyEncoding.TryEncode(startKey, startKeyBuffer, out startKeyLength))
            {
                var newLength = startKeyBuffer.Length * 2;
                ArrayPool<byte>.Shared.Return(startKeyBuffer);
                startKeyBuffer = ArrayPool<byte>.Shared.Rent(newLength);
            }
        }
        if (endKey != null)
        {
            var initialBufferSize = kv.KeyEncoding.GetMaxEncodedByteCount(endKey);
            endKeyBuffer = ArrayPool<byte>.Shared.Rent(initialBufferSize);
            while (!kv.KeyEncoding.TryEncode(endKey, endKeyBuffer, out endKeyLength))
            {
                var newLength = startKeyBuffer.Length * 2;
                ArrayPool<byte>.Shared.Return(endKeyBuffer);
                endKeyBuffer = ArrayPool<byte>.Shared.Rent(newLength);
            }
        }

        try
        {
            return await kv.GetRangeAsync(
                startKeyBuffer != null ? startKeyBuffer[..startKeyLength] : KeyRange.Unbound,
                endKeyBuffer != null ? endKeyBuffer[..endKeyLength] : KeyRange.Unbound,
                startKeyExclusive,
                endKeyExclusive,
                sortOrder,
                cancellationToken);
        }
        finally
        {
            if (startKeyBuffer != null) ArrayPool<byte>.Shared.Return(startKeyBuffer);
            if (endKeyBuffer != null) ArrayPool<byte>.Shared.Return(endKeyBuffer);
        }
    }

    public static int CountRange<TKey>(
        this IKeyValueStore kv,
        TKey? startKey,
        TKey? endKey,
        bool startKeyExclusive,
        bool endKeyExclusive)
        where TKey : IComparable<TKey>
    {
        var startKeyBuffer = startKey != null
            ? stackalloc byte[kv.KeyEncoding.GetMaxEncodedByteCount(startKey)]
            : [];

        var endKeyBuffer = endKey != null
            ? stackalloc byte[kv.KeyEncoding.GetMaxEncodedByteCount(endKey)]
            : [];

        var startKeyLength = 0;
        var endKeyLength = 0;

        if (startKey != null)
        {
            while (!kv.KeyEncoding.TryEncode(startKey, startKeyBuffer, out startKeyLength))
            {
                startKeyBuffer = stackalloc byte[startKeyBuffer.Length * 2];
            }
        }
        if (endKey != null)
        {
            while (!kv.KeyEncoding.TryEncode(endKey, endKeyBuffer, out endKeyLength))
            {
                endKeyBuffer = stackalloc byte[endKeyBuffer.Length * 2];
            }
        }

        return kv.CountRange(

            startKeyLength > 0 ? startKeyBuffer[..startKeyLength] : KeyRange.Unbound,
            endKeyLength > 0 ? endKeyBuffer[..endKeyLength] : KeyRange.Unbound,
            startKeyExclusive,
            endKeyExclusive);
    }

    public static async ValueTask<int> CountRangeAsync<TKey>(
        this IKeyValueStore kv,
        TKey? startKey,
        TKey? endKey,
        bool startKeyExclusive,
        bool endKeyExclusive,
        CancellationToken cancellationToken = default)
        where TKey : IComparable<TKey>
    {
        byte[]? startKeyBuffer = null;
        byte[]? endKeyBuffer = null;

        var startKeyLength = 0;
        var endKeyLength = 0;

        if (startKey != null)
        {
            var initialBufferSize = kv.KeyEncoding.GetMaxEncodedByteCount(startKey);
            startKeyBuffer = ArrayPool<byte>.Shared.Rent(initialBufferSize);
            while (!kv.KeyEncoding.TryEncode(startKey, startKeyBuffer, out startKeyLength))
            {
                var newLength = startKeyBuffer.Length * 2;
                ArrayPool<byte>.Shared.Return(startKeyBuffer);
                startKeyBuffer = ArrayPool<byte>.Shared.Rent(newLength);
            }
        }
        if (endKey != null)
        {
            var initialBufferSize = kv.KeyEncoding.GetMaxEncodedByteCount(endKey);
            endKeyBuffer = ArrayPool<byte>.Shared.Rent(initialBufferSize);
            while (!kv.KeyEncoding.TryEncode(endKey, endKeyBuffer, out endKeyLength))
            {
                var newLength = startKeyBuffer.Length * 2;
                ArrayPool<byte>.Shared.Return(endKeyBuffer);
                endKeyBuffer = ArrayPool<byte>.Shared.Rent(newLength);
            }
        }

        try
        {
            return await kv.CountRangeAsync(
                startKeyBuffer != null ? startKeyBuffer[..startKeyLength] : KeyRange.Unbound,
                endKeyBuffer != null ? endKeyBuffer[..endKeyLength] : KeyRange.Unbound,
                startKeyExclusive,
                endKeyExclusive,
                cancellationToken);
        }
        finally
        {
            if (startKeyBuffer != null) ArrayPool<byte>.Shared.Return(startKeyBuffer);
            if (endKeyBuffer != null) ArrayPool<byte>.Shared.Return(endKeyBuffer);
        }
    }

    public static RangeResult GetAll(this IKeyValueStore kv, ReadOnlySpan<byte> key, SortOrder sortOrder = SortOrder.Ascending)
    {
        return kv.GetRange(key, key, sortOrder: sortOrder);
    }

    public static RangeResult GetAll<TKey>(this IKeyValueStore kv, TKey key, SortOrder sortOrder = SortOrder.Ascending)
        where TKey : IComparable<TKey>
    {
        return kv.GetRange(key, key, sortOrder: sortOrder);
    }

    public static ValueTask<RangeResult> GetAllAsync(
        this IKeyValueStore kv,
        ReadOnlyMemory<byte> key,
        SortOrder sortOrder = SortOrder.Ascending,
        CancellationToken cancellationToken = default)
    {
        return kv.GetRangeAsync(key, key, sortOrder: sortOrder, cancellationToken: cancellationToken);
    }

    public static ValueTask<RangeResult> GetAllAsync<TKey>(
        this IKeyValueStore kv,
        TKey key,
        SortOrder sortOrder = SortOrder.Ascending,
        CancellationToken cancellationToken = default)
        where TKey : IComparable<TKey>
    {
        return kv.GetRangeAsync(key, key, sortOrder: sortOrder, cancellationToken: cancellationToken);
    }

    public static RangeResult GetRangeWithPrefix(
        this IKeyValueStore kv,
        ReadOnlySpan<byte> key,
        SortOrder sortOrder = SortOrder.Ascending)
    {
        if (key.Length <= 0) return RangeResult.Empty;

        // Strip trailing 0xFF bytes to find the last incrementable byte
        var endKeyLength = key.Length;
        while (endKeyLength > 0 && key[endKeyLength - 1] == 0xFF)
        {
            endKeyLength--;
        }

        // All bytes are 0xFF — prefix matches everything from key onward
        if (endKeyLength == 0)
        {
            return kv.GetRange(key, KeyRange.Unbound, sortOrder: sortOrder);
        }

        Span<byte> endKey = stackalloc byte[endKeyLength];
        key[..endKeyLength].CopyTo(endKey);
        endKey[endKeyLength - 1]++;
        return kv.GetRange(key, endKey, endKeyExclusive: true, sortOrder: sortOrder);
    }

    public static RangeResult GetRangeWithPrefix<TKey>(
        this IKeyValueStore kv,
        TKey key,
        SortOrder sortOrder = SortOrder.Ascending)
        where TKey : IComparable<TKey>
    {
        var bufferLength = kv.KeyEncoding.GetMaxEncodedByteCount(key);
        Span<byte> buffer = stackalloc byte[bufferLength];
        kv.KeyEncoding.TryEncode(key, buffer, out var bytesWritten);
        return kv.GetRangeWithPrefix(buffer[..bytesWritten], sortOrder);
    }

    public static async ValueTask<RangeResult> GetRangeWithPrefixAsync(
        this IKeyValueStore kv,
        ReadOnlyMemory<byte> key,
        SortOrder sortOrder = SortOrder.Ascending,
        CancellationToken cancellationToken = default)
    {
        if (key.Length <= 0) return RangeResult.Empty;

        var keySpan = key.Span;

        // Strip trailing 0xFF bytes
        var endKeyLength = keySpan.Length;
        while (endKeyLength > 0 && keySpan[endKeyLength - 1] == 0xFF)
        {
            endKeyLength--;
        }

        // All bytes are 0xFF — prefix matches everything from key onward
        if (endKeyLength == 0)
        {
            return await kv.GetRangeAsync(key, KeyRange.Unbound, sortOrder: sortOrder, cancellationToken: cancellationToken);
        }

        var endKeyBuffer = ArrayPool<byte>.Shared.Rent(endKeyLength);
        try
        {
            keySpan[..endKeyLength].CopyTo(endKeyBuffer);
            endKeyBuffer[endKeyLength - 1]++;
            return await kv.GetRangeAsync(key, endKeyBuffer.AsMemory(0, endKeyLength), endKeyExclusive: true, sortOrder: sortOrder, cancellationToken: cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(endKeyBuffer);
        }
    }

    public static async ValueTask<RangeResult> GetRangeWithPrefixAsync<TKey>(
        this IKeyValueStore kv,
        TKey key,
        SortOrder sortOrder = SortOrder.Ascending,
        CancellationToken cancellationToken = default)
        where TKey : IComparable<TKey>
    {
        var initialBufferSize = kv.KeyEncoding.GetMaxEncodedByteCount(key);
        var buffer = ArrayPool<byte>.Shared.Rent(initialBufferSize);
        int bytesWritten;
        while (!kv.KeyEncoding.TryEncode(key, buffer, out bytesWritten))
        {
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
        }
        try
        {
            return await kv.GetRangeWithPrefixAsync(buffer.AsMemory(0, bytesWritten), sortOrder, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
