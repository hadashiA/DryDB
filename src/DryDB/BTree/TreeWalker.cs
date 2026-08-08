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

/// <summary>
/// Non-generic base of <see cref="TreeWalker{TComparer}"/>. Holders (tables, index
/// queries, iterators) reference this type so that the comparer genericity stays out
/// of the public API; the virtual dispatch cost is one call per query, while the
/// per-comparison dispatch is specialized away in the derived class.
/// </summary>
abstract class TreeWalker
{
    public PageNumber RootPageNumber { get; }
    public PageCache PageCache { get; }
    public IKeyEncoding KeyEncoding { get; }

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
    private protected readonly struct PageLease(IPageEntry page, bool owned)
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

    private protected TreeWalker(
        PageNumber rootPageNumber,
        PageCache pageCache,
        IKeyEncoding keyEncoding)
    {
        RootPageNumber = rootPageNumber;
        PageCache = pageCache;
        KeyEncoding = keyEncoding;
    }

    /// <summary>
    /// Creates a walker specialized for the encoding: built-in encodings get a struct
    /// comparer instantiation (fully devirtualized search loops), anything else falls
    /// back to <see cref="FallbackKeyComparer"/>. Every instantiation is spelled out
    /// here so AOT (IL2CPP) generates them statically.
    /// </summary>
    internal static TreeWalker Create(
        PageNumber rootPageNumber,
        PageCache pageCache,
        IKeyEncoding keyEncoding)
    {
        return keyEncoding switch
        {
            Int64LittleEndianEncoding =>
                new TreeWalker<Int64KeyComparer>(rootPageNumber, pageCache, keyEncoding, default),
            AsciiOrdinalEncoding =>
                new TreeWalker<AsciiKeyComparer>(rootPageNumber, pageCache, keyEncoding, default),
#if NET9_0_OR_GREATER
            Uuidv7KeyEncoding =>
                new TreeWalker<Uuidv7KeyComparer>(rootPageNumber, pageCache, keyEncoding, default),
#endif
            DuplicateKeyEncoding duplicate =>
                new TreeWalker<DuplicateKeyComparer>(rootPageNumber, pageCache, keyEncoding, new DuplicateKeyComparer(duplicate.SourceEncoding)),
            _ =>
                new TreeWalker<FallbackKeyComparer>(rootPageNumber, pageCache, keyEncoding, new FallbackKeyComparer(keyEncoding)),
        };
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
    private protected PageLease GetPage(PageNumber pageNumber)
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

    private protected ValueTask<PageLease> GetPageAsync(PageNumber pageNumber, CancellationToken cancellationToken)
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
    private protected bool TryGetPage(PageNumber pageNumber, out PageLease lease)
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

    public abstract SingleValueResult Get(ReadOnlySpan<byte> key);

    public abstract ValueTask<SingleValueResult> GetAsync(
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempt the whole lookup against cached pages only. Returns false when any page
    /// on the path (including an overflow page) is not cached and IO would be required.
    /// </summary>
    internal abstract bool TryGetFromCache(scoped ReadOnlySpan<byte> key, out SingleValueResult result);

    public RangeIterator CreateIterator(IteratorDirection iteratorDirection = IteratorDirection.Forward) =>
        new(this, iteratorDirection);

    /// <summary>
    /// Descend to the leaf that satisfies <paramref name="op"/> for the key. On success
    /// the returned page carries a caller-owned reference.
    /// </summary>
    internal abstract bool Search(
        scoped ReadOnlySpan<byte> key,
        SearchOperator op,
        out IPageEntry page,
        out int index);

    internal abstract ValueTask<(IPageEntry? Page, int Index)> SearchAsync(
        ReadOnlyMemory<byte> key,
        SearchOperator op,
        CancellationToken cancellationToken);

    public abstract RangeResult GetRange(
        ReadOnlySpan<byte> startKey,
        ReadOnlySpan<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        SortOrder sortOrder = SortOrder.Ascending);

    public abstract ValueTask<RangeResult> GetRangeAsync(
        ReadOnlyMemory<byte> startKey,
        ReadOnlyMemory<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        SortOrder sortOrder = SortOrder.Ascending,
        CancellationToken cancellationToken = default);

    public abstract int CountRange(
        ReadOnlySpan<byte> startKey,
        ReadOnlySpan<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false);

    public abstract ValueTask<int> CountRangeAsync(
        ReadOnlyMemory<byte> startKey,
        ReadOnlyMemory<byte> endKey,
        bool startKeyExclusive = false,
        bool endKeyExclusive = false,
        CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Cache-only variant of <see cref="ResolveValue"/>: never loads the overflow page.
    /// </summary>
    private protected bool TryResolveBlobFromCache(ReadOnlySpan<byte> pageSpan, int valueOffset, out PageSlice slice)
    {
        var blobPageNumberValue = Unsafe.ReadUnaligned<long>(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(pageSpan), valueOffset));
        if (!PageCache.TryGet(new PageNumber(blobPageNumberValue), out var blobPage))
        {
            slice = default;
            return false;
        }

        var blobLength = blobPage.GetLength() - BlobDataOffset;
        slice = new PageSlice(blobPage, BlobDataOffset, blobLength);
        return true;
    }

    internal SingleValueResult GetMinValue()
    {
        var minLeaf = GetMinLeaf();
        if (!minLeaf.HasValue)
        {
            return SingleValueResult.Empty;
        }

        var (page, entryIndex) = minLeaf.Value;
        var header = NodeHeader.Parse(page.Memory.Span);
        var leafNode = new LeafNodeReader(page.Memory.Span, header.EntryCount, header.HasKeyDigests, header.HasEytzingerDigests);
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
        var header = NodeHeader.Parse(page.Memory.Span);
        var leafNode = new LeafNodeReader(page.Memory.Span, header.EntryCount, header.HasKeyDigests, header.HasEytzingerDigests);
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

                var internalNode = new InternalNodeReader(pageSpan, header.EntryCount, header.HasKeyDigests, header.HasEytzingerDigests);
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

                var internalNode = new InternalNodeReader(pageSpan, header.EntryCount, header.HasKeyDigests, header.HasEytzingerDigests);
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

    private protected void ValidateRange(ReadOnlySpan<byte> startKey, ReadOnlySpan<byte> endKey)
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
