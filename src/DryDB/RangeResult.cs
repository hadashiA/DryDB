using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace DryDB;

public class RangeResult : IDisposable, IEnumerable<ReadOnlyMemory<byte>>
{
    static readonly ConcurrentQueue<RangeResult> Pool = new();

    public static readonly RangeResult Empty = new();

    internal static RangeResult Rent()
    {
        if (Pool.TryDequeue(out var result))
        {
            return result;
        }
        return new RangeResult();
    }

    public int Count => list.Count;

    public ReadOnlyMemory<byte> this[int i] => list[i];

    readonly List<ReadOnlyMemory<byte>> list = [];
    readonly List<IPageEntry> referencePages = [];
    IPageEntry? lastReferencedPage;

    internal void Add(IPageEntry page, int start, int length)
    {
        // Rows are appended leaf by leaf, so consecutive rows usually share the page.
        // Retain once per run instead of once per row (a 100-row range used to cost
        // ~200 interlocked ops; now it costs one pair per page).
        if (!ReferenceEquals(page, lastReferencedPage))
        {
            page.Retain();
            referencePages.Add(page);
            lastReferencedPage = page;
        }
        list.Add(page.Memory.Slice(start, length));
    }

    public void Dispose()
    {
        if (this == Empty) return;

        foreach (var referencePage in referencePages)
        {
            referencePage.Release();
        }
        list.Clear();
        referencePages.Clear();
        lastReferencedPage = null;
        Pool.Enqueue(this);
    }

    public List<ReadOnlyMemory<byte>>.Enumerator GetEnumerator() => list.GetEnumerator();
    IEnumerator<ReadOnlyMemory<byte>> IEnumerable<ReadOnlyMemory<byte>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
