using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DryDB.Internal;

namespace DryDB.BTree;

enum SearchOperator
{
    Equal,
    LowerBound,
    UpperBound,
}

class TreeWalker
{
    public PageNumber RootPageNumber { get; }
    public PageCache PageCache { get; }
    public IKeyEncoding KeyEncoding { get; }

    readonly IKeyEncoding comparer;
    readonly bool supportsKeyDigest;

    // The root page is touched by every lookup. Keep one retained reference here so the
    // hot path can skip the cache lookup + refcount traffic for it. The pin lives until
    // the owning database is disposed (see ReleasePinnedRoot).
    IPageEntry? pinnedRoot;

    static readonly int BlobDataOffset = Unsafe.SizeOf<PageHeader>() + Unsafe.SizeOf<NodeHeader>();

    /// <summary>
    /// A page acquired for a tree walk. <see cref="Owned"/> is false when the page is
    /// served from the pinned root, in which case the walk holds no reference of its
    /// own and must not release it (use <see cref="Take"/> to hand the page out).
    /// </summary>
    readonly struct PageLease(IPageEntry page, bool owned)
    {
        public IPageEntry Page => page;
        public bool Owned => owned;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Release()
        {
            if (owned) page.Release();
        }

        /// <summary>
        /// Take a caller-owned reference to the page (for handing it out beyond the walk).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IPageEntry Take()
        {
            if (!owned) page.Retain();
            return page;
        }
    }

    internal TreeWalker(
        PageNumber rootPageNumber,
        PageCache pageCache,
        IKeyEncoding keyEncoding)
    {
        RootPageNumber = rootPageNumber;
        PageCache = pageCache;
        KeyEncoding = keyEncoding;

        // optimize
        comparer = keyEncoding switch
        {
            AsciiOrdinalEncoding => AsciiOrdinalEncoding.Instance,
            Int64LittleEndianEncoding => Int64LittleEndianEncoding.Instance,
            _ => keyEncoding
        };
        supportsKeyDigest = comparer.SupportsKeyDigest;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool TryGetKeyDigest(scoped ReadOnlySpan<byte> key, out ulong digest)
    {
        if (supportsKeyDigest && key.Length > 0)
        {
            digest = comparer.GetKeyDigest(key);
            return true;
        }
        digest = 0;
        return false;
    }

    /// <summary>
    /// Drop the pinned root reference. Required for page buffers over unmanaged memory
    /// (e.g. the Unity NativeArray loader), which are only freed when their refcount
    /// reaches zero.
    /// </summary>
    internal void ReleasePinnedRoot()
    {
        var root = Interlocked.Exchange(ref pinnedRoot, null);
        root?.Release();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    PageLease GetPage(PageNumber pageNumber)
    {
        if (pageNumber == RootPageNumber)
        {
            var root = pinnedRoot;
            if (root != null)
            {
                return new PageLease(root, false);
            }
            return PinRoot(PageCache.GetOrLoad(pageNumber));
        }
        return new PageLease(PageCache.GetOrLoad(pageNumber), true);
    }

    ValueTask<PageLease> GetPageAsync(PageNumber pageNumber, CancellationToken cancellationToken)
    {
        if (pageNumber == RootPageNumber)
        {
            var root = pinnedRoot;
            if (root != null)
            {
                return new ValueTask<PageLease>(new PageLease(root, false));
            }
            return PinRootAsync(pageNumber, cancellationToken);
        }
        return GetOwnedAsync(pageNumber, cancellationToken);

        async ValueTask<PageLease> PinRootAsync(PageNumber rootPageNumber, CancellationToken ct)
        {
            var page = await PageCache.GetOrLoadAsync(rootPageNumber, ct).ConfigureAwait(false);
            return PinRoot(page);
        }

        async ValueTask<PageLease> GetOwnedAsync(PageNumber pn, CancellationToken ct)
        {
            return new PageLease(await PageCache.GetOrLoadAsync(pn, ct).ConfigureAwait(false), true);
        }
    }

    PageLease PinRoot(IPageEntry page)
    {
        // Transfer the freshly acquired reference to the pin. If another thread won the
        // race, keep using our own reference as a normal owned lease.
        if (Interlocked.CompareExchange(ref pinnedRoot, page, null) == null)
        {
            return new PageLease(page, false);
        }
        return new PageLease(page, true);
    }

    /// <summary>
    /// Cache-only variant of <see cref="GetPage"/>: never loads. Used by the synchronous
    /// fast path of the async APIs.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool TryGetPage(PageNumber pageNumber, out PageLease lease)
    {
        if (pageNumber == RootPageNumber)
        {
            var root = pinnedRoot;
            if (root != null)
            {
                lease = new PageLease(root, false);
                return true;
            }
            if (PageCache.TryGet(pageNumber, out var rootPage))
            {
                lease = PinRoot(rootPage);
                return true;
            }
            lease = default;
            return false;
        }

        if (PageCache.TryGet(pageNumber, out var page))
        {
            lease = new PageLease(page, true);
            return true;
        }
        lease = default;
        return false;
    }

    public SingleValueResult Get(ReadOnlySpan<byte> key)
    {
        var hasKeyDigest = TryGetKeyDigest(key, out var keyDigest);
        var pageNumber = RootPageNumber;
        while (true)
        {
            var lease = GetPage(pageNumber);
            var pageSpan = lease.Page.Memory.Span;
            var header = NodeHeader.Parse(pageSpan);
            if (header.NodeKind == NodeKind.Internal)
            {
                var internalNode = new InternalNodeReader(pageSpan, header.EntryCount, header.HasKeyDigests);
                var descended = internalNode.TrySearch(key, comparer, keyDigest, hasKeyDigest, out pageNumber);
                lease.Release();
                if (!descended)
                {
                    return SingleValueResult.Empty;
                }
            }
            else // Leaf
            {
                var leafNode = new LeafNodeReader(pageSpan, header.EntryCount, header.HasKeyDigests);
                if (leafNode.TryFindValue(key, comparer, keyDigest, hasKeyDigest, out _, out var valueOffset, out var valueLength))
                {
                    if (LeafNodeReader.IsOverflow(valueLength))
                    {
                        var resolved = ResolveValue(lease.Page, valueOffset, valueLength);
                        lease.Release();
                        return new SingleValueResult(resolved, true);
                    }
                    return new SingleValueResult(new PageSlice(lease.Take(), valueOffset, valueLength), true);
                }

                lease.Release();
                return SingleValueResult.Empty;
            }
        }
    }

    public ValueTask<SingleValueResult> GetAsync(
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken = default)
    {
        // When every page on the path is already cached (the common case), complete
        // synchronously — going through the async state machine costs ~4x the walk.
        if (TryGetFromCache(key.Span, out var result))
        {
            return new ValueTask<SingleValueResult>(result);
        }
        return GetSlowAsync(key, cancellationToken);
    }

    /// <summary>
    /// Attempt the whole lookup against cached pages only. Returns false when any page
    /// on the path (including an overflow page) is not cached and IO would be required.
    /// </summary>
    internal bool TryGetFromCache(scoped ReadOnlySpan<byte> key, out SingleValueResult result)
    {
        var hasKeyDigest = TryGetKeyDigest(key, out var keyDigest);
        var pageNumber = RootPageNumber;
        while (true)
        {
            if (!TryGetPage(pageNumber, out var lease))
            {
                result = default;
                return false;
            }

            var pageSpan = lease.Page.Memory.Span;
            var header = NodeHeader.Parse(pageSpan);
            if (header.NodeKind == NodeKind.Internal)
            {
                var descended = new InternalNodeReader(pageSpan, header.EntryCount, header.HasKeyDigests)
                    .TrySearch(key, comparer, keyDigest, hasKeyDigest, out pageNumber);
                lease.Release();
                if (!descended)
                {
                    result = SingleValueResult.Empty;
                    return true;
                }
            }
            else // Leaf
            {
                var leafNode = new LeafNodeReader(pageSpan, header.EntryCount, header.HasKeyDigests);
                if (leafNode.TryFindValue(key, comparer, keyDigest, hasKeyDigest, out _, out var valueOffset, out var valueLength))
                {
                    if (LeafNodeReader.IsOverflow(valueLength))
                    {
                        var blobPageNumberValue = Unsafe.ReadUnaligned<long>(
                            ref Unsafe.Add(ref MemoryMarshal.GetReference(pageSpan), valueOffset));
                        if (!PageCache.TryGet(new PageNumber(blobPageNumberValue), out var blobPage))
                        {
                            lease.Release();
                            result = default;
                            return false;
                        }

                        lease.Release();
                        var blobLength = blobPage.GetLength() - BlobDataOffset;
                        result = new SingleValueResult(new PageSlice(blobPage, BlobDataOffset, blobLength), true);
                        return true;
                    }
                    result = new SingleValueResult(new PageSlice(lease.Take(), valueOffset, valueLength), true);
                    return true;
                }

                lease.Release();
                result = SingleValueResult.Empty;
                return true;
            }
        }
    }

    async ValueTask<SingleValueResult> GetSlowAsync(
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        var hasKeyDigest = TryGetKeyDigest(key.Span, out var keyDigest);
        var pageNumber = RootPageNumber;
        while (true)
        {
            var lease = await GetPageAsync(pageNumber, cancellationToken).ConfigureAwait(false);
            var header = NodeHeader.Parse(lease.Page.Memory.Span);
            if (header.NodeKind == NodeKind.Internal)
            {
                var descended = new InternalNodeReader(lease.Page.Memory.Span, header.EntryCount, header.HasKeyDigests)
                    .TrySearch(key.Span, comparer, keyDigest, hasKeyDigest, out pageNumber);
                lease.Release();
                if (!descended)
                {
                    return SingleValueResult.Empty;
                }
            }
            else // Leaf
            {
                if (new LeafNodeReader(lease.Page.Memory.Span, header.EntryCount, header.HasKeyDigests)
                    .TryFindValue(key.Span, comparer, keyDigest, hasKeyDigest, out _, out var valueOffset, out var valueLength))
                {
                    if (LeafNodeReader.IsOverflow(valueLength))
                    {
                        var resolved = await ResolveValueAsync(lease.Page, valueOffset, valueLength, cancellationToken)
                            .ConfigureAwait(false);
                        lease.Release();
                        return new SingleValueResult(resolved, true);
                    }
                    return new SingleValueResult(new PageSlice(lease.Take(), valueOffset, valueLength), true);
                }

                lease.Release();
                return SingleValueResult.Empty;
            }
        }
    }

    public RangeIterator CreateIterator(IteratorDirection iteratorDirection = IteratorDirection.Forward) =>
        new(this, iteratorDirection);

    /// <summary>
    /// Descend to the leaf that satisfies <paramref name="op"/> for the key. On success
    /// the returned page carries a caller-owned reference.
    /// </summary>
    internal bool Search(
        scoped ReadOnlySpan<byte> key,
        SearchOperator op,
        out IPageEntry page,
        out int index)
    {
        var hasKeyDigest = TryGetKeyDigest(key, out var keyDigest);
        var pageNumber = RootPageNumber;
        while (true)
        {
            var lease = GetPage(pageNumber);
            var pageSpan = lease.Page.Memory.Span;
            var header = NodeHeader.Parse(pageSpan);
            if (header.NodeKind == NodeKind.Internal)
            {
                var internalNode = new InternalNodeReader(pageSpan, header.EntryCount, header.HasKeyDigests);
                var descended = internalNode.TrySearch(key, comparer, keyDigest, hasKeyDigest, out pageNumber);
                lease.Release();
                if (!descended)
                {
                    page = null!;
                    index = default;
                    return false;
                }
            }
            else // Leaf
            {
                var leafNode = new LeafNodeReader(pageSpan, header.EntryCount, header.HasKeyDigests);
                if (leafNode.TrySearch(key, op, comparer, keyDigest, hasKeyDigest, out index))
                {
                    page = lease.Take();
                    return true;
                }

                // A bound miss on the leaf means every entry is below the bound. For
                // LowerBound/UpperBound the answer, if it exists, is the first entry
                // of the right sibling.
                if (op != SearchOperator.Equal && !header.RightSiblingPageNumber.IsEmpty)
                {
                    var siblingPageNumber = header.RightSiblingPageNumber;
                    lease.Release();
                    page = PageCache.GetOrLoad(siblingPageNumber);
                    index = 0;
                    return true;
                }

                lease.Release();
                page = null!;
                index = default;
                return false;
            }
        }
    }

    internal async ValueTask<(IPageEntry? Page, int Index)> SearchAsync(
        ReadOnlyMemory<byte> key,
        SearchOperator op,
        CancellationToken cancellationToken)
    {
        var hasKeyDigest = TryGetKeyDigest(key.Span, out var keyDigest);
        var pageNumber = RootPageNumber;
        while (true)
        {
            var lease = await GetPageAsync(pageNumber, cancellationToken).ConfigureAwait(false);
            var header = NodeHeader.Parse(lease.Page.Memory.Span);
            if (header.NodeKind == NodeKind.Internal)
            {
                var descended = new InternalNodeReader(lease.Page.Memory.Span, header.EntryCount, header.HasKeyDigests)
                    .TrySearch(key.Span, comparer, keyDigest, hasKeyDigest, out pageNumber);
                lease.Release();
                if (!descended)
                {
                    return (null, default);
                }
            }
            else // Leaf
            {
                if (new LeafNodeReader(lease.Page.Memory.Span, header.EntryCount, header.HasKeyDigests)
                    .TrySearch(key.Span, op, comparer, keyDigest, hasKeyDigest, out var index))
                {
                    return (lease.Take(), index);
                }

                // A bound miss on the leaf means every entry is below the bound. For
                // LowerBound/UpperBound the answer, if it exists, is the first entry
                // of the right sibling.
                if (op != SearchOperator.Equal && !header.RightSiblingPageNumber.IsEmpty)
                {
                    var siblingPageNumber = header.RightSiblingPageNumber;
                    lease.Release();
                    var sibling = await PageCache.GetOrLoadAsync(siblingPageNumber, cancellationToken)
                        .ConfigureAwait(false);
                    return (sibling, 0);
                }

                lease.Release();
                return (null, default);
            }
        }
    }

    public RangeResult GetRange(
        ReadOnlySpan<byte> startKey,
        ReadOnlySpan<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        SortOrder sortOrder = SortOrder.Ascending)
    {
        ValidateRange(startKey, endKey);

        return sortOrder == SortOrder.Descending
            ? GetRangeDescending(startKey, endKey, startKeyExclusive, endKeyExclusive)
            : GetRangeAscending(startKey, endKey, startKeyExclusive, endKeyExclusive);
    }

    RangeResult GetRangeAscending(
        ReadOnlySpan<byte> startKey,
        ReadOnlySpan<byte> endKey,
        bool startKeyExclusive,
        bool endKeyExclusive)
    {
        int entryIndex;
        IPageEntry page;

        // find start position
        if (startKey.IsEmpty)
        {
            var minLeaf = GetMinLeaf();
            if (!minLeaf.HasValue)
            {
                return RangeResult.Empty;
            }
            page = minLeaf.Value.Page;
            entryIndex = 0;
        }
        else if (!Search(
                     startKey,
                     startKeyExclusive ? SearchOperator.UpperBound : SearchOperator.LowerBound,
                     out page,
                     out entryIndex))
        {
            return RangeResult.Empty;
        }

        var hasEndKeyDigest = TryGetKeyDigest(endKey, out var endKeyDigest);
        var result = RangeResult.Rent();

        while (true)
        {
            var currentPage = page;
            try
            {
                var pageSpan = currentPage.Memory.Span;
                var header = NodeHeader.Parse(pageSpan);
                if (header.NodeKind != NodeKind.Leaf)
                {
                    throw new InvalidOperationException("Invalid node kind");
                }

                var leafNode = new LeafNodeReader(pageSpan, header.EntryCount, header.HasKeyDigests);

                // Entries within a leaf are sorted: locate the end bound with one binary
                // search instead of comparing every entry.
                var stopIndex = header.EntryCount;
                var endsInThisLeaf = false;
                if (!endKey.IsEmpty)
                {
                    var op = endKeyExclusive ? SearchOperator.LowerBound : SearchOperator.UpperBound;
                    if (leafNode.TrySearch(endKey, op, comparer, endKeyDigest, hasEndKeyDigest, out var boundIndex))
                    {
                        stopIndex = boundIndex;
                        endsInThisLeaf = true;
                    }
                }

                while (entryIndex < stopIndex)
                {
                    leafNode.GetAt(entryIndex, out var pageOffset, out var keyLength, out var valueLength);

                    if (LeafNodeReader.IsOverflow(valueLength))
                    {
                        var resolved = ResolveValue(currentPage, pageOffset + keyLength, valueLength);
                        result.Add(resolved.Page, resolved.Start, resolved.Length);
                        resolved.Page.Release();
                    }
                    else
                    {
                        result.Add(currentPage, pageOffset + keyLength, valueLength);
                    }
                    entryIndex++;
                }

                // next node
                if (endsInThisLeaf || header.RightSiblingPageNumber.IsEmpty)
                {
                    return result;
                }

                page = PageCache.GetOrLoad(header.RightSiblingPageNumber);
                entryIndex = 0;
            }
            finally
            {
                currentPage.Release();
            }
        }
    }

    RangeResult GetRangeDescending(
        ReadOnlySpan<byte> startKey,
        ReadOnlySpan<byte> endKey,
        bool startKeyExclusive,
        bool endKeyExclusive)
    {
        var start = FindDescendingStart(endKey, endKeyExclusive);
        if (!start.HasValue)
        {
            return RangeResult.Empty;
        }

        var page = start.Value.Page;
        var entryIndex = start.Value.EntryIndex;
        var hasStartKeyDigest = TryGetKeyDigest(startKey, out var startKeyDigest);
        var result = RangeResult.Rent();

        while (true)
        {
            var currentPage = page;
            try
            {
                var pageSpan = currentPage.Memory.Span;
                var header = NodeHeader.Parse(pageSpan);
                if (header.NodeKind != NodeKind.Leaf)
                {
                    throw new InvalidOperationException("Invalid node kind");
                }

                var leafNode = new LeafNodeReader(pageSpan, header.EntryCount, header.HasKeyDigests);

                // Entries within a leaf are sorted: locate the start bound with one
                // binary search instead of comparing every entry.
                var boundIndex = 0;
                if (!startKey.IsEmpty)
                {
                    var op = startKeyExclusive ? SearchOperator.UpperBound : SearchOperator.LowerBound;
                    if (!leafNode.TrySearch(startKey, op, comparer, startKeyDigest, hasStartKeyDigest, out boundIndex))
                    {
                        // Everything in (and left of) this leaf is below the start bound.
                        return result;
                    }
                }

                while (entryIndex >= boundIndex)
                {
                    leafNode.GetAt(entryIndex, out var pageOffset, out var keyLength, out var valueLength);

                    if (LeafNodeReader.IsOverflow(valueLength))
                    {
                        var resolved = ResolveValue(currentPage, pageOffset + keyLength, valueLength);
                        result.Add(resolved.Page, resolved.Start, resolved.Length);
                        resolved.Page.Release();
                    }
                    else
                    {
                        result.Add(currentPage, pageOffset + keyLength, valueLength);
                    }
                    entryIndex--;
                }

                // previous node (left sibling)
                if (boundIndex > 0 || header.LeftSiblingPageNumber.IsEmpty)
                {
                    return result;
                }

                page = PageCache.GetOrLoad(header.LeftSiblingPageNumber);
                entryIndex = NodeHeader.Parse(page.Memory.Span).EntryCount - 1;
            }
            finally
            {
                currentPage.Release();
            }
        }
    }

    public ValueTask<RangeResult> GetRangeAsync(
        ReadOnlyMemory<byte> startKey,
        ReadOnlyMemory<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        SortOrder sortOrder = SortOrder.Ascending,
        CancellationToken cancellationToken = default)
    {
        ValidateRange(startKey, endKey);

        return sortOrder == SortOrder.Descending
            ? GetRangeDescendingAsync(startKey, endKey, startKeyExclusive, endKeyExclusive, cancellationToken)
            : GetRangeAscendingAsync(startKey, endKey, startKeyExclusive, endKeyExclusive, cancellationToken);
    }

    async ValueTask<RangeResult> GetRangeAscendingAsync(
        ReadOnlyMemory<byte> startKey,
        ReadOnlyMemory<byte> endKey,
        bool startKeyExclusive,
        bool endKeyExclusive,
        CancellationToken cancellationToken)
    {
        int entryIndex;
        IPageEntry page;

        // find start position
        if (startKey.IsEmpty)
        {
            var minLeaf = GetMinLeaf();
            if (!minLeaf.HasValue)
            {
                return RangeResult.Empty;
            }
            page = minLeaf.Value.Page;
            entryIndex = 0;
        }
        else
        {
            (var startPage, entryIndex) = await SearchAsync(
                startKey,
                startKeyExclusive ? SearchOperator.UpperBound : SearchOperator.LowerBound,
                cancellationToken).ConfigureAwait(false);
            if (startPage == null)
            {
                return RangeResult.Empty;
            }
            page = startPage;
        }

        var hasEndKeyDigest = TryGetKeyDigest(endKey.Span, out var endKeyDigest);
        var result = RangeResult.Rent();

        while (true)
        {
            var currentPage = page;
            try
            {
                var header = NodeHeader.Parse(currentPage.Memory.Span);
                if (header.NodeKind != NodeKind.Leaf)
                {
                    throw new InvalidOperationException("Invalid node kind");
                }

                // Entries within a leaf are sorted: locate the end bound with one binary
                // search instead of comparing every entry.
                var stopIndex = header.EntryCount;
                var endsInThisLeaf = false;
                if (!endKey.IsEmpty)
                {
                    var op = endKeyExclusive ? SearchOperator.LowerBound : SearchOperator.UpperBound;
                    if (new LeafNodeReader(currentPage.Memory.Span, header.EntryCount, header.HasKeyDigests)
                        .TrySearch(endKey.Span, op, comparer, endKeyDigest, hasEndKeyDigest, out var boundIndex))
                    {
                        stopIndex = boundIndex;
                        endsInThisLeaf = true;
                    }
                }

                while (entryIndex < stopIndex)
                {
                    int pageOffset;
                    ushort keyLength, valueLength;
                    new LeafNodeReader(currentPage.Memory.Span, header.EntryCount, header.HasKeyDigests)
                        .GetAt(entryIndex, out pageOffset, out keyLength, out valueLength);

                    if (LeafNodeReader.IsOverflow(valueLength))
                    {
                        var resolved = await ResolveValueAsync(currentPage, pageOffset + keyLength, valueLength, cancellationToken)
                            .ConfigureAwait(false);
                        result.Add(resolved.Page, resolved.Start, resolved.Length);
                        resolved.Page.Release();
                    }
                    else
                    {
                        result.Add(currentPage, pageOffset + keyLength, valueLength);
                    }

                    entryIndex++;
                }

                // next node
                if (endsInThisLeaf || header.RightSiblingPageNumber.IsEmpty)
                {
                    return result;
                }

                page = await PageCache.GetOrLoadAsync(header.RightSiblingPageNumber, cancellationToken)
                    .ConfigureAwait(false);
                entryIndex = 0;
            }
            finally
            {
                currentPage.Release();
            }
        }
    }

    async ValueTask<RangeResult> GetRangeDescendingAsync(
        ReadOnlyMemory<byte> startKey,
        ReadOnlyMemory<byte> endKey,
        bool startKeyExclusive,
        bool endKeyExclusive,
        CancellationToken cancellationToken)
    {
        var start = await FindDescendingStartAsync(endKey, endKeyExclusive, cancellationToken).ConfigureAwait(false);
        if (!start.HasValue)
        {
            return RangeResult.Empty;
        }

        var page = start.Value.Page;
        var entryIndex = start.Value.EntryIndex;
        var hasStartKeyDigest = TryGetKeyDigest(startKey.Span, out var startKeyDigest);
        var result = RangeResult.Rent();

        while (true)
        {
            var currentPage = page;
            try
            {
                var header = NodeHeader.Parse(currentPage.Memory.Span);
                if (header.NodeKind != NodeKind.Leaf)
                {
                    throw new InvalidOperationException("Invalid node kind");
                }

                // Entries within a leaf are sorted: locate the start bound with one
                // binary search instead of comparing every entry.
                var boundIndex = 0;
                if (!startKey.IsEmpty)
                {
                    var op = startKeyExclusive ? SearchOperator.UpperBound : SearchOperator.LowerBound;
                    if (!new LeafNodeReader(currentPage.Memory.Span, header.EntryCount, header.HasKeyDigests)
                        .TrySearch(startKey.Span, op, comparer, startKeyDigest, hasStartKeyDigest, out boundIndex))
                    {
                        // Everything in (and left of) this leaf is below the start bound.
                        return result;
                    }
                }

                while (entryIndex >= boundIndex)
                {
                    int pageOffset;
                    ushort keyLength, valueLength;
                    new LeafNodeReader(currentPage.Memory.Span, header.EntryCount, header.HasKeyDigests)
                        .GetAt(entryIndex, out pageOffset, out keyLength, out valueLength);

                    if (LeafNodeReader.IsOverflow(valueLength))
                    {
                        var resolved = await ResolveValueAsync(currentPage, pageOffset + keyLength, valueLength, cancellationToken)
                            .ConfigureAwait(false);
                        result.Add(resolved.Page, resolved.Start, resolved.Length);
                        resolved.Page.Release();
                    }
                    else
                    {
                        result.Add(currentPage, pageOffset + keyLength, valueLength);
                    }

                    entryIndex--;
                }

                // previous node (left sibling)
                if (boundIndex > 0 || header.LeftSiblingPageNumber.IsEmpty)
                {
                    return result;
                }

                page = await PageCache.GetOrLoadAsync(header.LeftSiblingPageNumber, cancellationToken)
                    .ConfigureAwait(false);
                entryIndex = NodeHeader.Parse(page.Memory.Span).EntryCount - 1;
            }
            finally
            {
                currentPage.Release();
            }
        }
    }

    public int CountRange(
        ReadOnlySpan<byte> startKey,
        ReadOnlySpan<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false)
    {
        ValidateRange(startKey, endKey);

        int entryIndex;
        IPageEntry page;

        // find start position
        if (startKey.IsEmpty)
        {
            var minLeaf = GetMinLeaf();
            if (!minLeaf.HasValue)
            {
                return 0;
            }
            page = minLeaf.Value.Page;
            entryIndex = 0;
        }
        else if (!Search(
                     startKey,
                     startKeyExclusive ? SearchOperator.UpperBound : SearchOperator.LowerBound,
                     out page,
                     out entryIndex))
        {
            return 0;
        }

        var hasEndKeyDigest = TryGetKeyDigest(endKey, out var endKeyDigest);

        var count = 0;

        while (true)
        {
            // `page` is reassigned to the right sibling inside the loop; keep the
            // reference we own so that the finally releases the page we walked,
            // not the newly acquired one.
            var currentPage = page;
            try
            {
                var pageSpan = currentPage.Memory.Span;
                var header = NodeHeader.Parse(pageSpan);
                if (header.NodeKind != NodeKind.Leaf)
                {
                    throw new InvalidOperationException("Invalid node kind");
                }

                // Entries within a leaf are sorted: locate the end bound with one binary
                // search instead of comparing every entry.
                if (!endKey.IsEmpty)
                {
                    var leafNode = new LeafNodeReader(pageSpan, header.EntryCount, header.HasKeyDigests);
                    var op = endKeyExclusive ? SearchOperator.LowerBound : SearchOperator.UpperBound;
                    if (leafNode.TrySearch(endKey, op, comparer, endKeyDigest, hasEndKeyDigest, out var boundIndex))
                    {
                        // The range ends inside this leaf.
                        return count + Math.Max(0, boundIndex - entryIndex);
                    }
                }
                count += header.EntryCount - entryIndex;

                // next node
                if (header.RightSiblingPageNumber.IsEmpty)
                {
                    return count;
                }

                page = PageCache.GetOrLoad(header.RightSiblingPageNumber);
                entryIndex = 0;
            }
            finally
            {
                currentPage.Release();
            }
        }
    }

    public async ValueTask<int> CountRangeAsync(
        ReadOnlyMemory<byte> startKey,
        ReadOnlyMemory<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        CancellationToken cancellationToken = default)
    {
        ValidateRange(startKey, endKey);

        int entryIndex;
        IPageEntry page;

        // find start position
        if (startKey.IsEmpty)
        {
            var minLeaf = GetMinLeaf();
            if (!minLeaf.HasValue)
            {
                return 0;
            }
            page = minLeaf.Value.Page;
            entryIndex = 0;
        }
        else
        {
            (var startPage, entryIndex) = await SearchAsync(
                startKey,
                startKeyExclusive ? SearchOperator.UpperBound : SearchOperator.LowerBound,
                cancellationToken).ConfigureAwait(false);
            if (startPage == null)
            {
                return 0;
            }
            page = startPage;
        }

        var hasEndKeyDigest = TryGetKeyDigest(endKey.Span, out var endKeyDigest);

        var count = 0;

        while (true)
        {
            // `page` is reassigned to the right sibling inside the loop; keep the
            // reference we own so that the finally releases the page we walked,
            // not the newly acquired one.
            var currentPage = page;
            try
            {
                var pageSpan = currentPage.Memory.Span;
                var header = NodeHeader.Parse(pageSpan);
                if (header.NodeKind != NodeKind.Leaf)
                {
                    throw new InvalidOperationException("Invalid node kind");
                }

                // Entries within a leaf are sorted: locate the end bound with one binary
                // search instead of comparing every entry.
                if (!endKey.IsEmpty)
                {
                    var leafNode = new LeafNodeReader(pageSpan, header.EntryCount, header.HasKeyDigests);
                    var op = endKeyExclusive ? SearchOperator.LowerBound : SearchOperator.UpperBound;
                    if (leafNode.TrySearch(endKey.Span, op, comparer, endKeyDigest, hasEndKeyDigest, out var boundIndex))
                    {
                        // The range ends inside this leaf.
                        return count + Math.Max(0, boundIndex - entryIndex);
                    }
                }
                count += header.EntryCount - entryIndex;

                // next node
                if (header.RightSiblingPageNumber.IsEmpty)
                {
                    return count;
                }

                page = await PageCache.GetOrLoadAsync(header.RightSiblingPageNumber, cancellationToken)
                    .ConfigureAwait(false);
                entryIndex = 0;
            }
            finally
            {
                currentPage.Release();
            }
        }
    }

    internal PageSlice ResolveValue(IPageEntry leafPage, int valueOffset, ushort valueLength)
    {
        if (!LeafNodeReader.IsOverflow(valueLength))
        {
            return new PageSlice(leafPage, valueOffset, valueLength);
        }

        // Read the blob page number from the leaf's inline payload (8 bytes)
        var blobPageNumberValue = Unsafe.ReadUnaligned<long>(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(leafPage.Memory.Span), valueOffset));

        var blobPage = PageCache.GetOrLoad(new PageNumber(blobPageNumberValue));
        var blobLength = blobPage.GetLength() - BlobDataOffset;
        return new PageSlice(blobPage, BlobDataOffset, blobLength);
    }

    internal async ValueTask<PageSlice> ResolveValueAsync(IPageEntry leafPage, int valueOffset, ushort valueLength, CancellationToken ct)
    {
        if (!LeafNodeReader.IsOverflow(valueLength))
        {
            return new PageSlice(leafPage, valueOffset, valueLength);
        }

        var blobPageNumberValue = Unsafe.ReadUnaligned<long>(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(leafPage.Memory.Span), valueOffset));

        var blobPage = await PageCache.GetOrLoadAsync(new PageNumber(blobPageNumberValue), ct).ConfigureAwait(false);
        var blobLength = blobPage.GetLength() - BlobDataOffset;
        return new PageSlice(blobPage, BlobDataOffset, blobLength);
    }

    internal SingleValueResult GetMinValue()
    {
        var minLeaf = GetMinLeaf();
        if (!minLeaf.HasValue)
        {
            return SingleValueResult.Empty;
        }

        var (page, entryIndex) = minLeaf.Value;
        var leafNode = new LeafNodeReader(page.Memory.Span, NodeHeader.Parse(page.Memory.Span).EntryCount, NodeHeader.Parse(page.Memory.Span).HasKeyDigests);
        leafNode.GetAt(entryIndex, out var pageOffset, out var keyLength, out var valueLength);

        if (LeafNodeReader.IsOverflow(valueLength))
        {
            var resolved = ResolveValue(page, pageOffset + keyLength, valueLength);
            page.Release();
            return new SingleValueResult(resolved, true);
        }

        var pageSlice = new PageSlice(page, pageOffset + keyLength, valueLength);
        return new SingleValueResult(pageSlice, true);
    }

    internal SingleValueResult GetMaxValue()
    {
        var maxLeaf = GetMaxValueLeaf();
        if (!maxLeaf.HasValue)
        {
            return SingleValueResult.Empty;
        }

        var (page, entryIndex) = maxLeaf.Value;
        var leafNode = new LeafNodeReader(page.Memory.Span, NodeHeader.Parse(page.Memory.Span).EntryCount, NodeHeader.Parse(page.Memory.Span).HasKeyDigests);
        leafNode.GetAt(entryIndex, out var pageOffset, out var keyLength, out var valueLength);

        if (LeafNodeReader.IsOverflow(valueLength))
        {
            var resolved = ResolveValue(page, pageOffset + keyLength, valueLength);
            page.Release();
            return new SingleValueResult(resolved, true);
        }

        var pageSlice = new PageSlice(page, pageOffset + keyLength, valueLength);
        return new SingleValueResult(pageSlice, true);
    }

    internal (IPageEntry Page, int EntryIndex)? GetMinLeaf()
    {
        var pageNumber = RootPageNumber;
        while (true)
        {
            var lease = GetPage(pageNumber);
            var pageSpan = lease.Page.Memory.Span;
            var header = NodeHeader.Parse(pageSpan);
            if (header.NodeKind == NodeKind.Internal)
            {
                if (header.EntryCount <= 0)
                {
                    lease.Release();
                    return null;
                }

                var internalNode = new InternalNodeReader(pageSpan, header.EntryCount, header.HasKeyDigests);
                internalNode.GetAt(0, out _, out pageNumber);
                lease.Release();
            }
            else // Leaf
            {
                if (header.EntryCount <= 0)
                {
                    lease.Release();
                    return null;
                }
                return (lease.Take(), 0);
            }
        }
    }

    internal (IPageEntry Page, int EntryIndex)? GetMaxValueLeaf()
    {
        var pageNumber = RootPageNumber;
        while (true)
        {
            var lease = GetPage(pageNumber);
            var pageSpan = lease.Page.Memory.Span;
            var header = NodeHeader.Parse(pageSpan);
            if (header.NodeKind == NodeKind.Internal)
            {
                if (header.EntryCount <= 0)
                {
                    lease.Release();
                    return null;
                }

                var internalNode = new InternalNodeReader(pageSpan, header.EntryCount, header.HasKeyDigests);
                internalNode.GetAt(header.EntryCount - 1, out _, out pageNumber);
                lease.Release();
            }
            else // Leaf
            {
                if (header.EntryCount <= 0)
                {
                    lease.Release();
                    return null;
                }
                return (lease.Take(), header.EntryCount - 1);
            }
        }
    }

    (IPageEntry Page, int EntryIndex)? FindDescendingStart(
        ReadOnlySpan<byte> endKey,
        bool endKeyExclusive)
    {
        if (endKey.IsEmpty)
        {
            return GetMaxValueLeaf();
        }

        // Find the first entry beyond the end bound, then step one back.
        var op = endKeyExclusive ? SearchOperator.LowerBound : SearchOperator.UpperBound;
        if (!Search(endKey, op, out var page, out var entryIndex))
        {
            // Every entry satisfies the bound: start from the maximum.
            return GetMaxValueLeaf();
        }

        entryIndex--;
        if (entryIndex < 0)
        {
            // Need to go to the left sibling page
            var header = NodeHeader.Parse(page.Memory.Span);
            if (header.LeftSiblingPageNumber.IsEmpty)
            {
                page.Release();
                return null;
            }

            var leftPageNumber = header.LeftSiblingPageNumber;
            page.Release();

            page = PageCache.GetOrLoad(leftPageNumber);
            entryIndex = NodeHeader.Parse(page.Memory.Span).EntryCount - 1;
        }
        return (page, entryIndex);
    }

    async ValueTask<(IPageEntry Page, int EntryIndex)?> FindDescendingStartAsync(
        ReadOnlyMemory<byte> endKey,
        bool endKeyExclusive,
        CancellationToken cancellationToken)
    {
        if (endKey.IsEmpty)
        {
            return GetMaxValueLeaf();
        }

        var op = endKeyExclusive ? SearchOperator.LowerBound : SearchOperator.UpperBound;
        var (page, entryIndex) = await SearchAsync(endKey, op, cancellationToken).ConfigureAwait(false);
        if (page == null)
        {
            return GetMaxValueLeaf();
        }

        entryIndex--;
        if (entryIndex < 0)
        {
            var header = NodeHeader.Parse(page.Memory.Span);
            if (header.LeftSiblingPageNumber.IsEmpty)
            {
                page.Release();
                return null;
            }

            var leftPageNumber = header.LeftSiblingPageNumber;
            page.Release();

            page = await PageCache.GetOrLoadAsync(leftPageNumber, cancellationToken).ConfigureAwait(false);
            entryIndex = NodeHeader.Parse(page.Memory.Span).EntryCount - 1;
        }
        return (page, entryIndex);
    }

    void ValidateRange(ReadOnlySpan<byte> startKey, ReadOnlySpan<byte> endKey)
    {
        if (!startKey.IsEmpty && !endKey.IsEmpty)
        {
            if (KeyEncoding.Compare(startKey, endKey) > 0)
            {
                throw new ArgumentException("startKey must be less than or equal to endKey");
            }
        }
    }

    void ValidateRange(ReadOnlyMemory<byte> startKey, ReadOnlyMemory<byte> endKey)
    {
        if (!startKey.IsEmpty && !endKey.IsEmpty)
        {
            if (KeyEncoding.Compare(startKey, endKey) > 0)
            {
                throw new ArgumentException("startKey must be less than or equal to endKey");
            }
        }
    }
}
