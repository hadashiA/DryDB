using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DryDB.Internal;

/// <summary>
/// Page cache (S3-FIFO)
/// </summary>
public sealed class PageCache : IDisposable
{
    enum QueueTag : byte
    {
        None,
        S,
        M
    }

    class Entry : IPageEntry
    {
        public required PageNumber PageNumber { get; init; }
        public required IMemoryOwner<byte>? Buffer { get; init; }
        public QueueTag Tag { get; set; }

        public int RefCount;
        public int Frequency;

        public ReadOnlyMemory<byte> Memory
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Buffer!.Memory;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Retain()
        {
            Interlocked.Increment(ref RefCount);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRetainIfAlive()
        {
            while (true)
            {
                var current = Volatile.Read(ref RefCount);
                if (current <= 0)
                    return false;

                var next = current + 1;

                int original = Interlocked.CompareExchange(ref RefCount, next, current);
                if (original == current)
                    return true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Release()
        {
            if (Interlocked.Decrement(ref RefCount) == 0)
            {
                Buffer?.Dispose();
            }
        }
    }

    // readonly ConcurrentDictionary<PageNumber, Entry> entries = new();
    readonly ConcurrentDictionary<PageNumber, Entry> map;
    readonly ConcurrentDictionary<PageNumber, byte> ghost;

    readonly MpscRingQueue<Entry> sQueue;
    readonly MpscRingQueue<Entry> mQueue;

    readonly IPageLoader pageLoader;
    readonly int capacity;
    readonly IPageFilter[]? filters;
    readonly int sTargetSize;
    readonly int mTargetSize;

    int approxSSize;
    int approxMSize;
    int evicting; // 0 or 1
    bool disposed;

    internal PageCache(
        IPageLoader pageLoader,
        int capacity,
        IPageFilter[]? filters,
        double smallFraction = 0.2,
        double ghostFraction = 1.0)
    {
        this.pageLoader = pageLoader;
        this.capacity = capacity;
        this.filters = filters;

        sTargetSize = Math.Max(2, (int)(capacity * smallFraction));
        mTargetSize = capacity - sTargetSize;

        map = new ConcurrentDictionary<PageNumber, Entry>(
            Environment.ProcessorCount,
            capacity);

        ghost = new ConcurrentDictionary<PageNumber, byte>(
            Environment.ProcessorCount,
            (int)(mTargetSize * ghostFraction));

        var fifoCap = 1;
        while (fifoCap < capacity) fifoCap <<= 1;

        sQueue = new MpscRingQueue<Entry>(fifoCap);
        mQueue = new MpscRingQueue<Entry>(fifoCap);
    }

    public void Dispose()
    {
        lock (map)
        {
            if (disposed) return;

            foreach (var t in map.Values)
            {
                t.Release();
            }
            disposed = true;
        }
    }

    public bool TryGet(PageNumber pageNumber, out IPageEntry page)
    {
        if (map.TryGetValue(pageNumber, out var entry))
        {
            if (!entry.TryRetainIfAlive())
            {
                page = null!;
                return false;
            }

            // freq++（max: 3）
            while (true)
            {
                var frequency = entry.Frequency;
                if (frequency >= 3) break;
                if (Interlocked.CompareExchange(ref entry.Frequency, frequency + 1, frequency) == frequency)
                {
                    break;
                }
            }
            page = entry;
            return true;
        }
        page = default!;
        return false;
    }

    public void Load(PageNumber pageNumber)
    {
        var buffer = pageLoader.ReadPage(pageNumber);
        AddEntry(pageNumber, buffer);
    }

    public async ValueTask LoadAsync(PageNumber pageNumber, CancellationToken cancellationToken = default)
    {
        var buffer = await pageLoader.ReadPageAsync(pageNumber, filters, cancellationToken);
        AddEntry(pageNumber, buffer);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void AddEntry(PageNumber pageNumber, IMemoryOwner<byte> buffer)
    {
        var entry = new Entry
        {
            PageNumber = pageNumber,
            Buffer = buffer,
            Frequency = 1,
            Tag = QueueTag.None,
            RefCount = 1
        };

        if (!map.TryAdd(pageNumber, entry))
        {
            // race
            return;
        }

        var inGhost = ghost.ContainsKey(pageNumber);
        entry.Tag = inGhost ? QueueTag.M : QueueTag.S;

        if (inGhost)
        {
            // Resurrected from Ghost -> to M Queue
            ghost.TryRemove(pageNumber, out _);
            if (mQueue.TryEnqueue(entry))
            {
                Interlocked.Increment(ref approxMSize);
            }
        }
        else
        {
            // New → S queue
            if (sQueue.TryEnqueue(entry))
            {
                Interlocked.Increment(ref approxSSize);
            }
        }

        // Try triggering an eviction if the capacity seems to be exceeded.
        if (map.Count > capacity)
        {
            TryStartEvict();
        }
    }

    void TryStartEvict()
    {
        // evict with only one thread at a time
        if (Interlocked.CompareExchange(ref evicting, 1, 0) != 0)
            return;

        try
        {
            while (map.Count > capacity)
            {
                EvictOne();
            }
        }
        finally
        {
            Volatile.Write(ref evicting, 0);
        }
    }

    void EvictOne()
    {
        // If the approximate size of S is greater than the target, prioritize S; otherwise, prioritize M.
        if (Volatile.Read(ref approxSSize) >= sTargetSize)
        {
            if (!EvictFromS())
            {
                EvictFromM();
            }
        }
        else
        {
            if (!EvictFromM())
            {
                EvictFromS();
            }
        }
    }

    bool EvictFromS()
    {
        while (sQueue.TryDequeue(out var e))
        {
            // Skip if it's already removed from the map or moved to S.
            if (!map.TryGetValue(e.PageNumber, out var current) ||
                current != e ||
                current.Tag != QueueTag.S)
            {
                continue;
            }

            Interlocked.Decrement(ref approxSSize);

            // If freq > 1, promote to M.
            if (Volatile.Read(ref current.Frequency) > 1)
            {
                current.Frequency = 0;
                current.Tag = QueueTag.M;
                if (mQueue.TryEnqueue(current))
                {
                    Interlocked.Increment(ref approxMSize);
                }

                // There might be an overflow of M, so EvictFromM if necessary.
                if (Volatile.Read(ref approxMSize) > mTargetSize)
                {
                    EvictFromM();
                }
                return true;
            }

            // Send to ghost
            if (map.TryRemove(current.PageNumber, out _))
            {
                current.Release();
            }
            if (ghost.Count > mTargetSize)
            {
                // Approximate by discarding one element
                foreach (var k in ghost.Keys)
                {
                    ghost.TryRemove(k, out _);
                    break;
                }
            }
            ghost.TryAdd(current.PageNumber, 0);
            return true;
        }

        return false;
    }

    bool EvictFromM()
    {
        while (mQueue.TryDequeue(out var e))
        {
            if (!map.TryGetValue(e.PageNumber, out var current) ||
                current != e ||
                current.Tag != QueueTag.M)
            {
                continue;
            }

            Interlocked.Decrement(ref approxMSize);

            var f = Volatile.Read(ref current.Frequency);
            if (f > 0)
            {
                // Second chance: re-insert after increasing frequency
                Interlocked.Decrement(ref current.Frequency);
                if (mQueue.TryEnqueue(current))
                {
                    Interlocked.Increment(ref approxMSize);
                }
                return true;
            }
            // Complete expulsion (not into ghosting here)
            if (map.TryRemove(current.PageNumber, out _))
            {
                e.Release();
            }
            return true;
        }

        return false;
    }
}