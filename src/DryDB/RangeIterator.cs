using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DryDB.BTree;

namespace DryDB;

public enum IteratorDirection
{
    Forward,
    Backward
}

public class RangeIterator :
    IEnumerable<ReadOnlyMemory<byte>>,
    IEnumerator<ReadOnlyMemory<byte>>,
    IAsyncEnumerable<ReadOnlyMemory<byte>>,
    IAsyncEnumerator<ReadOnlyMemory<byte>>
{
    object? IEnumerator.Current => Current;

    public ReadOnlyMemory<byte> CurrentKey
    {
        get
        {
            var header = NodeHeader.Parse(currentPage!.Memory.Span);
            if (header.Kind != NodeKind.Leaf)
            {
                throw new InvalidOperationException("Invalid node kind");
            }

            var reader = new LeafNodeReader(currentPage.Memory.Span, header.EntryCount);
            reader.GetAt(currentEntryIndex, out var pageOffset, out var keyLength, out _);
            return currentPage.Memory.Slice(pageOffset, keyLength);
        }
    }

    public ReadOnlyMemory<byte> CurrentValue
    {
        get
        {
            currentOverflowPage?.Release();
            currentOverflowPage = null;

            var header = NodeHeader.Parse(currentPage!.Memory.Span);
            if (header.Kind != NodeKind.Leaf)
            {
                throw new InvalidOperationException("Invalid node kind");
            }
            var reader = new LeafNodeReader(currentPage.Memory.Span, header.EntryCount);
            reader.GetAt(currentEntryIndex, out var pageOffset, out var keyLength, out var valueLength);

            if (LeafNodeReader.IsOverflow(valueLength))
            {
                var resolved = treeWalker.ResolveValue(currentPage, pageOffset + keyLength, valueLength);
                currentOverflowPage = resolved.Page;
                return resolved.Page.Memory.Slice(resolved.Start, resolved.Length);
            }

            return currentPage.Memory.Slice(pageOffset + keyLength, valueLength);
        }
    }

    public ReadOnlyMemory<byte> Current => CurrentValue;

    // iterator state
    readonly TreeWalker treeWalker;
    readonly IteratorDirection direction;
    IPageEntry? currentPage;
    IPageEntry? currentOverflowPage;
    int currentEntryIndex;

    internal RangeIterator(
        TreeWalker treeWalker,
        IteratorDirection iteratorDirection = IteratorDirection.Forward)
    {
        this.treeWalker = treeWalker;
        this.direction = iteratorDirection;
    }

    public RangeIterator GetEnumerator() => this;
    IEnumerator<ReadOnlyMemory<byte>> IEnumerable<ReadOnlyMemory<byte>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public RangeIterator GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
    IAsyncEnumerator<ReadOnlyMemory<byte>> IAsyncEnumerable<ReadOnlyMemory<byte>>.GetAsyncEnumerator(
        CancellationToken cancellationToken) =>
        GetAsyncEnumerator(cancellationToken);

    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    public void Dispose()
    {
        currentOverflowPage?.Release();
        currentOverflowPage = null;
        currentPage?.Release();
        currentPage = null;
    }

    public void Reset()
    {
        currentOverflowPage?.Release();
        currentOverflowPage = null;
        currentPage?.Release();
        currentPage = null;
    }

    public bool TrySeek(ReadOnlySpan<byte> key)
    {
        PageNumber? nextPageNumber = treeWalker.RootPageNumber;
        IPageEntry page;
        int entryIndex;

        while (!treeWalker.TrySearch(
                   nextPageNumber.Value,
                   key,
                   SearchOperator.Equal,
                   out page,
                   out entryIndex,
                   out nextPageNumber))
        {
            if (!nextPageNumber.HasValue) return false;
            treeWalker.PageCache.Load(nextPageNumber.Value);
        }

        currentOverflowPage?.Release();
        currentOverflowPage = null;

        if (currentPage != page)
        {
            currentPage?.Release();
            currentPage = page;
        }
        currentEntryIndex = entryIndex;
        return true;
    }

    public async ValueTask<bool> TrySeekAsync(
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken = default)
    {
        PageNumber? nextPageNumber = treeWalker.RootPageNumber;
        IPageEntry page;
        int entryIndex;

        while (!treeWalker.TrySearch(
                   nextPageNumber.Value,
                   key.Span,
                   SearchOperator.Equal,
                   out page,
                   out entryIndex,
                   out nextPageNumber))
        {
            if (!nextPageNumber.HasValue) return false;
            await treeWalker.PageCache.LoadAsync(nextPageNumber.Value, cancellationToken);
        }

        currentOverflowPage?.Release();
        currentOverflowPage = null;

        if (currentPage != page)
        {
            currentPage?.Release();
            currentPage = page;
        }
        currentEntryIndex = entryIndex;
        return true;
    }

    public bool MoveNext()
    {
        return direction == IteratorDirection.Backward ? MoveNextBackward() : MoveNextForward();
    }

    bool MoveNextForward()
    {
        var pageCache = treeWalker.PageCache;

        // first item
        if (currentPage is null)
        {
            var minLeaf = treeWalker.GetMinLeaf();
            if (!minLeaf.HasValue)
            {
                return false;
            }

            currentPage = minLeaf.Value.Page;
            currentEntryIndex = 0;
            return true;
        }

        // tail of node
        var header = currentPage.GetNodeHeader();
        if (currentEntryIndex >= header.EntryCount - 1)
        {
            // check right node exists
            if (header.RightSiblingPageNumber.IsEmpty)
            {
                return false;
            }

            currentPage.Release();
            while (!pageCache.TryGet(header.RightSiblingPageNumber, out currentPage))
            {
                treeWalker.PageCache.Load(header.RightSiblingPageNumber);
            }

            header = currentPage.GetNodeHeader();
            if (header.Kind != NodeKind.Leaf)
            {
                throw new InvalidOperationException("Invalid node kind");
            }
            currentEntryIndex = 0;
            if (header.EntryCount < 0)
            {
                return false;
            }
        }
        else
        {
            currentEntryIndex++;
        }
        return true;
    }

    bool MoveNextBackward()
    {
        var pageCache = treeWalker.PageCache;

        // first item
        if (currentPage is null)
        {
            var maxLeaf = treeWalker.GetMaxValueLeaf();
            if (!maxLeaf.HasValue)
            {
                return false;
            }

            currentPage = maxLeaf.Value.Page;
            currentEntryIndex = maxLeaf.Value.EntryIndex;
            return true;
        }

        // head of node
        if (currentEntryIndex <= 0)
        {
            var header = currentPage.GetNodeHeader();
            // check left node exists
            if (header.LeftSiblingPageNumber.IsEmpty)
            {
                return false;
            }

            currentPage.Release();
            while (!pageCache.TryGet(header.LeftSiblingPageNumber, out currentPage))
            {
                treeWalker.PageCache.Load(header.LeftSiblingPageNumber);
            }

            var leftHeader = currentPage.GetNodeHeader();
            if (leftHeader.Kind != NodeKind.Leaf)
            {
                throw new InvalidOperationException("Invalid node kind");
            }
            currentEntryIndex = leftHeader.EntryCount - 1;
            if (leftHeader.EntryCount <= 0)
            {
                return false;
            }
        }
        else
        {
            currentEntryIndex--;
        }
        return true;
    }

    public async ValueTask<bool> MoveNextAsync()
    {
        return direction == IteratorDirection.Backward
            ? await MoveNextBackwardAsync().ConfigureAwait(false)
            : await MoveNextForwardAsync().ConfigureAwait(false);
    }

    async ValueTask<bool> MoveNextForwardAsync()
    {
        var pageCache = treeWalker.PageCache;

        // first item
        if (currentPage is null)
        {
            var minLeaf = treeWalker.GetMinLeaf();
            if (!minLeaf.HasValue)
            {
                return false;
            }

            currentPage = minLeaf.Value.Page;
            currentEntryIndex = 0;
            return true;
        }

        // tail of node
        var header = currentPage.GetNodeHeader();
        if (currentEntryIndex >= header.EntryCount - 1)
        {
            // check right node exists
            if (header.RightSiblingPageNumber.IsEmpty)
            {
                return false;
            }

            currentPage.Release();
            while (!pageCache.TryGet(header.RightSiblingPageNumber, out currentPage))
            {
                await treeWalker.PageCache.LoadAsync(header.RightSiblingPageNumber);
            }

            header = currentPage.GetNodeHeader();
            if (header.Kind != NodeKind.Leaf)
            {
                throw new InvalidOperationException("Invalid node kind");
            }
            currentEntryIndex = 0;
            if (header.EntryCount < 0)
            {
                return false;
            }
        }
        else
        {
            currentEntryIndex++;
        }
        return true;
    }

    async ValueTask<bool> MoveNextBackwardAsync()
    {
        var pageCache = treeWalker.PageCache;

        // first item
        if (currentPage is null)
        {
            var maxLeaf = treeWalker.GetMaxValueLeaf();
            if (!maxLeaf.HasValue)
            {
                return false;
            }

            currentPage = maxLeaf.Value.Page;
            currentEntryIndex = maxLeaf.Value.EntryIndex;
            return true;
        }

        // head of node
        if (currentEntryIndex <= 0)
        {
            var header = currentPage.GetNodeHeader();
            // check left node exists
            if (header.LeftSiblingPageNumber.IsEmpty)
            {
                return false;
            }

            currentPage.Release();
            while (!pageCache.TryGet(header.LeftSiblingPageNumber, out currentPage))
            {
                await treeWalker.PageCache.LoadAsync(header.LeftSiblingPageNumber);
            }

            var leftHeader = currentPage.GetNodeHeader();
            if (leftHeader.Kind != NodeKind.Leaf)
            {
                throw new InvalidOperationException("Invalid node kind");
            }
            currentEntryIndex = leftHeader.EntryCount - 1;
            if (leftHeader.EntryCount <= 0)
            {
                return false;
            }
        }
        else
        {
            currentEntryIndex--;
        }
        return true;
    }
}
