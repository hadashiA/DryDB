using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DryDB.Internal;

/// <summary>
/// Page cache (S3-FIFO).
/// </summary>
/// <remarks>
/// Pages are identified by their dense ordinal (format 1.3), so the whole "map" is a
/// plain array indexed by ordinal: a cache hit is one volatile array read plus one
/// refcount increment — no hashing, no key comparison, no per-entry node objects.
/// The ghost set is likewise an epoch array: an ordinal is "in ghost" while its
/// recorded eviction epoch lies within the last <c>ghostWindow</c> evictions.
/// </remarks>
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

        public QueueTag Tag { get; set; }

        public int RefCount;
        public int Frequency;

        IMemoryOwner<byte>? buffer;
        readonly ReadOnlyMemory<byte> memory;

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
            Interlocked.Increment(ref RefCount);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRetainIfAlive()
        {
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

    // ordinal -> live entry. This is the whole map.
    readonly Entry?[] entries;

    // ordinal -> epoch at which the page was last evicted from S (0 = never). The
    // page is "in ghost" while (ghostClock - epoch) < ghostWindow, which models the
    // classic bounded ghost FIFO without any allocation.
    readonly int[] ghostEpoch;
    int ghostClock;

    // ordinal -> file offset, used only when a page must actually be loaded.
    readonly long[] pageOffsets;

    readonly MpscRingQueue<Entry> sQueue;
    readonly MpscRingQueue<Entry> mQueue;

    readonly IPageLoader pageLoader;
    readonly int capacity;
    readonly IPageFilter[]? filters;
    readonly int sTargetSize;
    readonly int mTargetSize;
    readonly int ghostWindow;

    int approxCount;
    int approxSSize;
    int approxMSize;
    int evicting; // 0 or 1
    bool disposed;

    internal PageCache(
        IPageLoader pageLoader,
        long[] pageOffsets,
        int capacity,
        IPageFilter[]? filters,
        double smallFraction = 0.2,
        double ghostFraction = 1.0)
    {
        this.pageLoader = pageLoader;
        this.pageOffsets = pageOffsets;
        // The cache can never hold more pages than the file contains.
        this.capacity = capacity = Math.Max(2, Math.Min(capacity, pageOffsets.Length));
        this.filters = filters;

        sTargetSize = Math.Max(2, (int)(capacity * smallFraction));
        mTargetSize = Math.Max(1, capacity - sTargetSize);
        ghostWindow = Math.Max(1, (int)(mTargetSize * ghostFraction));

        entries = new Entry?[pageOffsets.Length];
        ghostEpoch = new int[pageOffsets.Length];

        var fifoCap = 1;
        while (fifoCap < capacity) fifoCap <<= 1;

        sQueue = new MpscRingQueue<Entry>(fifoCap);
        mQueue = new MpscRingQueue<Entry>(fifoCap);
    }

    public void Dispose()
    {
        lock (entries)
        {
            if (disposed) return;

            for (var i = 0; i < entries.Length; i++)
            {
                Interlocked.Exchange(ref entries[i], null)?.Release();
            }
            disposed = true;
        }
    }

    public bool TryGet(PageNumber pageNumber, out IPageEntry page)
    {
        var entry = Volatile.Read(ref entries[(int)pageNumber.Value]);
        if (entry != null)
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

            var buffer = pageLoader.ReadPage(pageOffsets[(int)pageNumber.Value], filters);
            if (TryPublish(pageNumber, buffer, out page))
            {
                return page;
            }

            // Lost the publish race: another thread's entry is in the slot. Our buffer
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

            var buffer = await pageLoader.ReadPageAsync(pageOffsets[(int)pageNumber.Value], filters, cancellationToken).ConfigureAwait(false);
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
        var entry = new Entry
        {
            PageNumber = pageNumber,
            Buffer = buffer,
            Frequency = 1,
            Tag = QueueTag.None,
            // One reference for the slot, one handed to the caller.
            RefCount = 2
        };

        var index = (int)pageNumber.Value;
        if (Interlocked.CompareExchange(ref entries[index], entry, null) != null)
        {
            page = null!;
            return false;
        }
        Interlocked.Increment(ref approxCount);

        var inGhost = IsInGhost(index);
        entry.Tag = inGhost ? QueueTag.M : QueueTag.S;

        if (inGhost)
        {
            // Resurrected from Ghost -> to M Queue
            Volatile.Write(ref ghostEpoch[index], 0);
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
        if (Volatile.Read(ref approxCount) > capacity)
        {
            TryStartEvict();
        }

        page = entry;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool IsInGhost(int index)
    {
        var epoch = Volatile.Read(ref ghostEpoch[index]);
        if (epoch == 0) return false;
        // Unsigned difference keeps the comparison valid across clock wrap-around.
        return (uint)(Volatile.Read(ref ghostClock) - epoch) < (uint)ghostWindow;
    }

    void RecordGhost(int index)
    {
        var epoch = Interlocked.Increment(ref ghostClock);
        // 0 is the "never evicted" sentinel; skip it on wrap-around.
        Volatile.Write(ref ghostEpoch[index], epoch == 0 ? 1 : epoch);
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
            // keep refreshing frequencies. Leaving the cache temporarily over capacity
            // is fine — the next Load retries the eviction.
            var attempts = capacity * 4;
            while (Volatile.Read(ref approxCount) > capacity && attempts-- > 0)
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
            var index = (int)e.PageNumber.Value;

            // Skip if it's already removed from the slot or moved to M.
            if (!ReferenceEquals(Volatile.Read(ref entries[index]), e) ||
                e.Tag != QueueTag.S)
            {
                continue;
            }

            Interlocked.Decrement(ref approxSSize);

            // If freq > 1, promote to M.
            if (Volatile.Read(ref e.Frequency) > 1)
            {
                e.Frequency = 0;
                e.Tag = QueueTag.M;
                if (mQueue.TryEnqueue(e))
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

            // Evict and send to ghost
            if (Interlocked.CompareExchange(ref entries[index], null, e) == e)
            {
                Interlocked.Decrement(ref approxCount);
                RecordGhost(index);
                e.Release();
            }
            return true;
        }

        return false;
    }

    bool EvictFromM()
    {
        while (mQueue.TryDequeue(out var e))
        {
            var index = (int)e.PageNumber.Value;

            if (!ReferenceEquals(Volatile.Read(ref entries[index]), e) ||
                e.Tag != QueueTag.M)
            {
                continue;
            }

            Interlocked.Decrement(ref approxMSize);

            var f = Volatile.Read(ref e.Frequency);
            if (f > 0)
            {
                // Second chance: re-insert after decreasing frequency
                Interlocked.Decrement(ref e.Frequency);
                if (mQueue.TryEnqueue(e))
                {
                    Interlocked.Increment(ref approxMSize);
                }
                return true;
            }
            // Complete expulsion (not into ghosting here)
            if (Interlocked.CompareExchange(ref entries[index], null, e) == e)
            {
                Interlocked.Decrement(ref approxCount);
                e.Release();
            }
            return true;
        }

        return false;
    }
}
