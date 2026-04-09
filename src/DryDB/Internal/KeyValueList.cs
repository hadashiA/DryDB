using System;
using System.Collections;
using System.Collections.Generic;

namespace DryDB.Internal;

abstract class KeyValueList(IKeyEncoding keyEncoding) : IEnumerable<KeyValuePair<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>>
{
    public static KeyValueList Create(IKeyEncoding keyEncoding, bool unique) => unique
        ? new UniqueKeyValueList(keyEncoding)
        : new DuplicateKeyValueList(keyEncoding);

    public abstract int Count { get; }
    public IKeyEncoding KeyEncoding => keyEncoding;

    public abstract void Add(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value);

    public void Add<TKey>(TKey key, ReadOnlyMemory<byte> value)
        where TKey : IComparable<TKey>
    {
        var initialBufferSize = keyEncoding.GetMaxEncodedByteCount(key);
        var buffer = new byte[initialBufferSize];
        int bytesWritten;
        while (!keyEncoding.TryEncode(key, buffer, out bytesWritten))
        {
            buffer = new byte[buffer.Length * 2];
        }
        Add(buffer.AsMemory(0, bytesWritten), value);
    }

    public abstract IEnumerator<KeyValuePair<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>> GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

class UniqueKeyValueList(IKeyEncoding keyEncoding) : KeyValueList(keyEncoding)
{
    public override int Count => list.Count;

    readonly SortedList<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> list = new(keyEncoding);

    public override IEnumerator<KeyValuePair<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>> GetEnumerator() => list.GetEnumerator();

    public override void Add(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value)
    {
        if (key.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(key), "key too large");

        if (!list.TryAdd(key, value))
        {
            throw new ArgumentException("duplicate key");
        }
    }
}

class DuplicateKeyValueList(IKeyEncoding keyEncoding) : KeyValueList(keyEncoding)
{
    public override int Count => count;

    readonly SortedList<ReadOnlyMemory<byte>, List<ReadOnlyMemory<byte>>> list = new(keyEncoding);
    int count;

    public override IEnumerator<KeyValuePair<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>> GetEnumerator()
    {
        foreach (var x in list)
        {
            for (var i = 0; i < x.Value.Count; i++)
            {
                var value = x.Value[i];
                var key = DuplicateKey.Generate(x.Key.Span, i);
                yield return new KeyValuePair<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>(key, value);
            }
        }
    }

    public override void Add(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value)
    {
        if (!list.TryGetValue(key, out var values))
        {
            values = [];
            list[key] = values;
        }
        values.Add(value);
        count++;
    }
}
