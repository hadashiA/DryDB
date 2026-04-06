using MessagePack;

namespace DryDB.MessagePack;

public class MessagePackReadOnlyTable<TValue>(
    ReadOnlyTable table,
    MessagePackSerializerOptions? options = null)
{
    public TValue? Get(ReadOnlySpan<byte> key)
    {
        using var result = table.Get(key);
        return result.HasValue
            ? MessagePackSerializer.Deserialize<TValue>(result.Value.Memory, options)
            : default;
    }

    public TValue? Get<TKey>(TKey key) where TKey : IComparable<TKey>
    {
        using var result = table.Get(key);
        return result.HasValue
            ? MessagePackSerializer.Deserialize<TValue>(result.Value.Memory, options)
            : default;
    }

    public async ValueTask<TValue> GetAsync(
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken = default)
    {
        using var result = await table.GetAsync(key, cancellationToken);
        if (!result.HasValue)
        {
            return default!;
        }
        return MessagePackSerializer.Deserialize<TValue>(result.Value.Memory, options, cancellationToken);
    }

    public async ValueTask<TValue> GetAsync<TKey>(
        TKey key,
        CancellationToken cancellationToken = default)
        where TKey : IComparable<TKey>
    {
        using var result = await table.GetAsync(key, cancellationToken);
        if (!result.HasValue)
        {
            return default!;
        }
        return MessagePackSerializer.Deserialize<TValue>(result.Value.Memory, options, cancellationToken);
    }

    public IReadOnlyList<TValue> GetRange(
        ReadOnlySpan<byte> startKey,
        ReadOnlySpan<byte> endKey,
        bool startKeyExclusive,
        bool endKeyExclusive,
        SortOrder sortOrder)
    {
        using var result = table.GetRange(startKey, endKey, startKeyExclusive, endKeyExclusive, sortOrder);
        if (result.Count <= 0)
        {
            return [];
        }
        var list = new List<TValue>(result.Count);
        foreach (var value in result)
        {
            var deserializedValue = MessagePackSerializer.Deserialize<TValue>(value, options);
            list.Add(deserializedValue);
        }
        return list;
    }

    public IReadOnlyList<TValue> GetRange<TKey>(
        TKey? startKey,
        TKey? endKey,
        bool startKeyExclusive,
        bool endKeyExclusive,
        SortOrder sortOrder) where TKey : IComparable<TKey>
    {
        using var result = table.GetRange(startKey, endKey, startKeyExclusive, endKeyExclusive, sortOrder);
        if (result.Count <= 0)
        {
            return [];
        }
        var list = new List<TValue>(result.Count);
        foreach (var value in result)
        {
            var deserializedValue = MessagePackSerializer.Deserialize<TValue>(value, options);
            list.Add(deserializedValue);
        }
        return list;
    }

    public async ValueTask<IReadOnlyList<TValue>> GetRangeAsync(
        ReadOnlyMemory<byte> startKey,
        ReadOnlyMemory<byte> endKey,
        bool startKeyExclusive,
        bool endKeyExclusive,
        SortOrder sortOrder,
        CancellationToken cancellationToken = default)
    {
        using var result = await table.GetRangeAsync(startKey, endKey, startKeyExclusive, endKeyExclusive, sortOrder, cancellationToken);
        if (result.Count <= 0)
        {
            return [];
        }
        var list = new List<TValue>(result.Count);
        foreach (var value in result)
        {
            var deserializedValue = MessagePackSerializer.Deserialize<TValue>(value, options, cancellationToken);
            list.Add(deserializedValue);
        }
        return list;
    }

    public async ValueTask<IReadOnlyList<TValue>> GetRangeAsync<TKey>(
        TKey? startKey,
        TKey? endKey,
        bool startKeyExclusive,
        bool endKeyExclusive,
        SortOrder sortOrder,
        CancellationToken cancellationToken = default) where TKey : IComparable<TKey>
    {
        using var result = await table.GetRangeAsync(startKey, endKey, startKeyExclusive, endKeyExclusive, sortOrder, cancellationToken);
        if (result.Count <= 0)
        {
            return [];
        }
        var list = new List<TValue>(result.Count);
        foreach (var value in result)
        {
            var deserializedValue = MessagePackSerializer.Deserialize<TValue>(value, options, cancellationToken);
            list.Add(deserializedValue);
        }
        return list;
    }
}