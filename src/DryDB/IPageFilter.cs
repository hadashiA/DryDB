using System;
using System.Buffers;
using System.Collections.Concurrent;

namespace DryDB;

public interface IPageFilter
{
    string Id { get; }

    void Encode(ReadOnlySpan<byte> input, IBufferWriter<byte> output);
    void Decode(ReadOnlySpan<byte> input, IBufferWriter<byte> output);
}

public static class PageFilterRegistry
{
    static readonly ConcurrentDictionary<string, IPageFilter> table = new();

    public static IPageFilter Resolve(string id) => table[id];

    public static void Register(IPageFilter filter)
    {
        if (!table.TryAdd(filter.Id, filter))
        {
            throw new ArgumentException($"The filter {filter.Id} already exists.");
        }
    }
}