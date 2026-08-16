using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DryDB.Internal;

namespace DryDB.BTree;

/// <summary>
/// Tree walker specialized per key comparer. TComparer is constrained to a struct so
/// the runtime generates one instantiation per comparer type: every
/// <c>comparer.Compare</c> in the search loops is a direct, inlinable call, and
/// <c>comparer.SupportsKeyDigest</c> folds to a constant (see
/// <see cref="IKeyComparer"/>).
/// </summary>
sealed class TreeWalker<TComparer> : TreeWalker
    where TComparer : struct, IKeyComparer
{
    // Not readonly: invoking interface members on a readonly field of an unconstrained
    // struct type parameter forces a defensive copy per call site.
    TComparer comparer;

    internal TreeWalker(
        PageNumber rootPageNumber,
        PageCache pageCache,
        IKeyEncoding keyEncoding,
        TComparer comparer)
        : base(rootPageNumber, pageCache, keyEncoding)
    {
        this.comparer = comparer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool TryGetKeyDigest(scoped ReadOnlySpan<byte> key, out ulong digest)
    {
        if (comparer.SupportsKeyDigest && key.Length > 0)
        {
            digest = comparer.GetKeyDigest(key);
            return true;
        }
        digest = 0;
        return false;
    }

    public override SingleValueResult Get(ReadOnlySpan<byte> key)
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

    public override ValueTask<SingleValueResult> GetAsync(
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

    internal override bool TryGetFromCache(scoped ReadOnlySpan<byte> key, out SingleValueResult result)
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
                        if (!TryResolveBlobFromCache(pageSpan, valueOffset, out var slice))
                        {
                            lease.Release();
                            result = default;
                            return false;
                        }

                        lease.Release();
                        result = new SingleValueResult(slice, true);
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

    internal override bool Search(
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

    internal override async ValueTask<(IPageEntry? Page, int Index)> SearchAsync(
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

    public override RangeResult GetRange(
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

    public override ValueTask<RangeResult> GetRangeAsync(
        ReadOnlyMemory<byte> startKey,
        ReadOnlyMemory<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        SortOrder sortOrder = SortOrder.Ascending,
        CancellationToken cancellationToken = default)
    {
        ValidateRange(startKey.Span, endKey.Span);

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

    public override int CountRange(
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

    public override async ValueTask<int> CountRangeAsync(
        ReadOnlyMemory<byte> startKey,
        ReadOnlyMemory<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        CancellationToken cancellationToken = default)
    {
        ValidateRange(startKey.Span, endKey.Span);

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
}
