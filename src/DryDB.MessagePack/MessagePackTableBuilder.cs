using System;
using MessagePack;

namespace DryDB.MessagePack;

public class MessagePackTableBuilder<TValue>(
    TableBuilder builder,
    MessagePackSerializerOptions? options = null)
{
    public void Append(ReadOnlyMemory<byte> key, TValue value)
    {
        var bytes = MessagePackSerializer.Serialize(value, options);
        builder.Append(key, bytes);
    }

    public void Append<TKey>(TKey key, TValue value)
        where TKey : IComparable<TKey>
    {
        var bytes = MessagePackSerializer.Serialize(value, options);
        builder.Append(key, bytes);
    }

    public void AddSecondaryIndex(
        string indexName,
        bool isUnique,
        IKeyEncoding keyEncoding,
        Func<ReadOnlyMemory<byte>, TValue, ReadOnlyMemory<byte>> indexFactory)
    {
        builder.AddSecondaryIndex(indexName, isUnique, keyEncoding, (key, value) =>
        {
            var serializedValue = MessagePackSerializer.Deserialize<TValue>(value, options);
            return indexFactory(key, serializedValue);
        });
    }

    public void AddSecondaryIndex<TIndex>(
        string indexName,
        bool isUnique,
        IKeyEncoding keyEncoding,
        Func<ReadOnlyMemory<byte>, TValue, TIndex> indexFactory)
        where TIndex : IComparable<TIndex>
    {
        builder.AddSecondaryIndex(indexName, isUnique, keyEncoding, (key, value) =>
        {
            var serializedValue = MessagePackSerializer.Deserialize<TValue>(value, options);
            return indexFactory(key, serializedValue);
        });
    }
}