using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
        public required IMemoryOwner<byte>? Buffer
        {
            get => buffer;
            init
            {
                buffer = value;
                // cache the Memory to avoid the virtual IMemoryOwner<byte>.Memory call per access
                memory = value?.Memory ?? default;
            }
        }

        /// <summary>
        /// When false, the buffer is left to the GC once nothing references the entry, and
        /// all reference counting is a no-op — reads become interlocked-free. Buffers over
        /// unmanaged memory (e.g. Unity's NativeArray loader) must always be reference
        /// counted so that they can be disposed deterministically.
        /// </summary>
        public required bool RefCounted
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => refCounted;
            init => refCounted = value;
        }

        public QueueTag Tag { get; set; }

        public int RefCount;
        public int Frequency;

        IMemoryOwner<byte>? buffer;
        readonly ReadOnlyMemory<byte> memory;
        readonly bool refCounted;

        public ReadOnlyMemory<byte> Memory
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => memory;
        }

        // Once the refcount reaches zero the entry is stamped with this bias so that
        // late optimistic increments (see TryRetainIfAlive) can never make a dead entry
        // look alive again. Far larger in magnitude than any possible reader count.
        const int DeadBias = int.MinValue / 2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Retain()
        {
            if (!refCounted) return;
            Interlocked.Increment(ref RefCount);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRetainIfAlive()
        {
            if (!refCounted) return true;

            // Optimistic fetch-add instead of a CAS loop: one atomic op that always
            // succeeds, which scales far better under contention (no retry storms).
            // The entry object itself is GC-managed, so touching the counter of a dead
            // entry is safe — only the buffer must not be used.
            //
            //   result >= 1 : the entry was alive (or a racing releaser is about to
            //                 attempt the dead-stamp CAS, which our increment defeats).
            //   result <= 0 : the entry was already stamped dead; undo and fail.
            var result = Interlocked.Increment(ref RefCount);
            if (result >= 1)
            {
                return true;
            }

            Interlocked.Decrement(ref RefCount);
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Release()
        {
            if (!refCounted) return;
            if (Interlocked.Decrement(ref RefCount) == 0)
            {
                // Stamp the entry dead before disposing. If a concurrent optimistic
                // retain slipped in after our decrement, the CAS fails and the entry
                // stays alive — that reader now owns it and will release it later.
                if (Interlocked.CompareExchange(ref RefCount, DeadBias, 0) == 0)
                {
                    Buffer?.Dispose();
                }
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
    readonly bool gcReclamation;
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
        double ghostFraction = 1.0,
        bool gcReclamation = true)
    {
        this.pageLoader = pageLoader;
        this.capacity = capacity;
        this.filters = filters;
        this.gcReclamation = gcReclamation;

        sTargetSize = Math.Max(2, (int)(capacity * smallFraction));
        mTargetSize = capacity - sTargetSize;

        // The dictionary grows on demand; preallocating buckets for the full capacity
        // (up to ~512k pages with the default CacheSize) wastes megabytes at open.
        map = new ConcurrentDictionary<PageNumber, Entry>(
            Environment.ProcessorCount,
            Math.Min(capacity, 4096));

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
            // Frequency is an approximate heuristic for S3-FIFO, so a single CAS attempt
            // is enough (no retry loop). The CAS must not be relaxed to a plain write:
            // a stale overwrite would cancel the evictor's second-chance decrement and
            // hot entries could never expire, livelocking the evict loop. With the CAS
            // the reader's bump simply loses when it races the evictor. Once saturated
            // at 3, readers stop writing the line entirely.
            var frequency = entry.Frequency;
            if (frequency < 3)
            {
                Interlocked.CompareExchange(ref entry.Frequency, frequency + 1, frequency);
            }
            page = entry;
            return true;
        }
        page = default!;
        return false;
    }

    /// <summary>
    /// Get the page from the cache, loading it if necessary. The returned entry always
    /// carries a reference owned by the caller (release it when done), so it stays valid
    /// even if the page is evicted immediately after — which makes reads safe under
    /// cache thrash. (The old load-then-relookup protocol could livelock: concurrent
    /// loaders kept evicting each other's pages before they could be looked up again.)
    /// </summary>
    public IPageEntry GetOrLoad(PageNumber pageNumber)
    {
        while (true)
        {
            if (TryGet(pageNumber, out var page))
            {
                return page;
            }

            var buffer = pageLoader.ReadPage(pageNumber, filters);
            if (TryPublish(pageNumber, buffer, out page))
            {
                return page;
            }

            // Lost the publish race: another thread's entry is in the map. Our buffer
            // was never published, so it can be disposed directly.
            buffer.Dispose();
        }
    }

    /// <inheritdoc cref="GetOrLoad"/>
    public async ValueTask<IPageEntry> GetOrLoadAsync(PageNumber pageNumber, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (TryGet(pageNumber, out var page))
            {
                return page;
            }

            var buffer = await pageLoader.ReadPageAsync(pageNumber, filters, cancellationToken).ConfigureAwait(false);
            if (TryPublish(pageNumber, buffer, out page))
            {
                return page;
            }

            buffer.Dispose();
        }
    }

    public void Load(PageNumber pageNumber)
    {
        GetOrLoad(pageNumber).Release();
    }

    public async ValueTask LoadAsync(PageNumber pageNumber, CancellationToken cancellationToken = default)
    {
        (await GetOrLoadAsync(pageNumber, cancellationToken).ConfigureAwait(false)).Release();
    }

    bool TryPublish(PageNumber pageNumber, IMemoryOwner<byte> buffer, out IPageEntry page)
    {
        // Unmanaged buffers must always be reference counted; array-backed buffers only
        // when deterministic pooling is requested (PageReclamation.ReferenceCounted).
        var refCounted = !gcReclamation || !MemoryMarshal.TryGetArray(buffer.Memory, out ArraySegment<byte> _);

        var entry = new Entry
        {
            PageNumber = pageNumber,
            Buffer = buffer,
            RefCounted = refCounted,
            Frequency = 1,
            Tag = QueueTag.None,
            // One reference for the map, one handed to the caller.
            RefCount = 2
        };

        if (!map.TryAdd(pageNumber, entry))
        {
            page = null!;
            return false;
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

        page = entry;
        return true;
    }

    void TryStartEvict()
    {
        // evict with only one thread at a time
        if (Interlocked.CompareExchange(ref evicting, 1, 0) != 0)
            return;

        try
        {
            // Bounded pass: second-chance re-insertions count as progress, so under
            // heavy concurrent access a single pass could otherwise spin while readers
            // keep refreshing frequencies. Leaving the map temporarily over capacity is
            // fine — the next Load retries the eviction.
            var attempts = capacity * 4;
            while (map.Count > capacity && attempts-- > 0)
            {
                // Stop when neither queue can make progress (e.g. entries raced out of
                // the queues); otherwise this would spin forever.
                if (!EvictOne()) break;
            }
        }
        finally
        {
            Volatile.Write(ref evicting, 0);
        }
    }

    bool EvictOne()
    {
        // If the approximate size of S is greater than the target, prioritize S; otherwise, prioritize M.
        if (Volatile.Read(ref approxSSize) >= sTargetSize)
        {
            return EvictFromS() || EvictFromM();
        }
        return EvictFromM() || EvictFromS();
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