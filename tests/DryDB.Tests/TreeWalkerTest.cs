using System;
using System.Threading.Tasks;
using DryDB.BTree;
using DryDB.Internal;

namespace DryDB.Tests;

[TestFixture]
public class TreeWalkerTest
{
    [Test]
    public async Task SearchOperator_Equal()
    {
        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key1"u8.ToArray(), "value1"u8.ToArray() },
                { "key2"u8.ToArray(), "value2"u8.ToArray() },
                { "key3"u8.ToArray(), "value3"u8.ToArray() },
                { "key4"u8.ToArray(), "value4"u8.ToArray() },
                { "key5"u8.ToArray(), "value5"u8.ToArray() },
                { "key6"u8.ToArray(), "value6"u8.ToArray() },
                { "key7"u8.ToArray(), "value7"u8.ToArray() },
                { "key8"u8.ToArray(), "value8"u8.ToArray() },
                { "key9"u8.ToArray(), "value9"u8.ToArray() },
            }, 128);

        await tree.PageCache.LoadAsync(tree.RootPageNumber);

        var result = tree.TrySearch(
            "key3"u8.ToArray(),
            SearchOperator.Equal,
            out _,
            out var index,
            out var nextPageNumber);

        Assert.That(result, Is.False);
        Assert.That(nextPageNumber.HasValue, Is.True);

        var pageNumber = nextPageNumber.Value;
        await tree.PageCache.LoadAsync(pageNumber);

        result = tree.TrySearch(
            "key3"u8.ToArray(),
            SearchOperator.Equal,
            out _,
            out index,
            out nextPageNumber);
        Assert.That(result, Is.True);
        Assert.That(nextPageNumber.HasValue, Is.False);

        Assert.That(tree.PageCache.TryGet(pageNumber, out var page), Is.True);

        var nodeHeader = NodeHeader.Parse(page.Memory.Span);
        var leafNode = new LeafNodeReader(page.Memory.Span, nodeHeader.EntryCount);

        leafNode.GetAt(index, out var pageOffset, out var keyLength, out var valueLength);
        var key = page.Memory.Span.Slice(pageOffset, keyLength);
        var value = page.Memory.Span.Slice(pageOffset + keyLength, valueLength);
        Assert.That(key.SequenceEqual("key3"u8), Is.True);
        Assert.That(value.SequenceEqual("value3"u8), Is.True);
    }

    [Test]
    public async Task SearchOperator_LowerBound()
    {
        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key01"u8.ToArray(), "value01"u8.ToArray() },
                { "key02"u8.ToArray(), "value02"u8.ToArray() },
                { "key03"u8.ToArray(), "value03"u8.ToArray() },
                { "key04"u8.ToArray(), "value04"u8.ToArray() },
                { "key05"u8.ToArray(), "value05"u8.ToArray() },
                { "key06"u8.ToArray(), "value06"u8.ToArray() },
                { "key07"u8.ToArray(), "value07"u8.ToArray() },
                { "key08"u8.ToArray(), "value08"u8.ToArray() },
                { "key09"u8.ToArray(), "value09"u8.ToArray() },
                { "key10"u8.ToArray(), "value10"u8.ToArray() },
                { "key11"u8.ToArray(), "value11"u8.ToArray() },
                { "key12"u8.ToArray(), "value12"u8.ToArray() },
                { "key13"u8.ToArray(), "value13"u8.ToArray() },
                { "key14"u8.ToArray(), "value14"u8.ToArray() },
                { "key15"u8.ToArray(), "value15"u8.ToArray() },
                { "key16"u8.ToArray(), "value16"u8.ToArray() },
            }, 128);

        await tree.PageCache.LoadAsync(tree.RootPageNumber);

        var result = tree.TrySearch(
            "key03"u8.ToArray(),
            SearchOperator.LowerBound,
            out _,
            out var pos,
            out var nextPageNumber);

        Assert.That(result, Is.False);
        Assert.That(nextPageNumber.HasValue, Is.True);

        await tree.PageCache.LoadAsync(nextPageNumber!.Value);

        var pageNumber = nextPageNumber.Value;

        result = tree.TrySearch(
            pageNumber,
            "key03"u8.ToArray(),
            SearchOperator.LowerBound,
            out _,
            out pos,
            out nextPageNumber);

        Assert.That(result, Is.True);
        Assert.That(nextPageNumber.HasValue, Is.False);

        Assert.That(tree.PageCache.TryGet(pageNumber, out var page), Is.True);

        var header = NodeHeader.Parse(page.Memory.Span);
        var leafNode = new LeafNodeReader(page.Memory.Span, header.EntryCount);

        leafNode.GetAt(pos, out var pageOffset, out var keyLength, out var valueLength);
        var key = page.Memory.Span.Slice(pageOffset, keyLength);
        var value = page.Memory.Span.Slice(pageOffset + keyLength, valueLength);
        Assert.That(key.SequenceEqual("key03"u8), Is.True);
        Assert.That(value.SequenceEqual("value03"u8), Is.True);
    }

    [Test]
    public async Task SearchOperator_UpperBound()
    {
        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key01"u8.ToArray(), "value01"u8.ToArray() },
                { "key02"u8.ToArray(), "value02"u8.ToArray() },
                { "key03"u8.ToArray(), "value03"u8.ToArray() },
                { "key04"u8.ToArray(), "value04"u8.ToArray() },
                { "key05"u8.ToArray(), "value05"u8.ToArray() },
                { "key06"u8.ToArray(), "value06"u8.ToArray() },
                { "key07"u8.ToArray(), "value07"u8.ToArray() },
                { "key08"u8.ToArray(), "value08"u8.ToArray() },
                { "key09"u8.ToArray(), "value09"u8.ToArray() },
                { "key10"u8.ToArray(), "value10"u8.ToArray() },
                { "key11"u8.ToArray(), "value11"u8.ToArray() },
                { "key12"u8.ToArray(), "value12"u8.ToArray() },
                { "key13"u8.ToArray(), "value13"u8.ToArray() },
                { "key14"u8.ToArray(), "value14"u8.ToArray() },
                { "key15"u8.ToArray(), "value15"u8.ToArray() },
                { "key16"u8.ToArray(), "value16"u8.ToArray() },
            }, 128);

        await tree.PageCache.LoadAsync(tree.RootPageNumber);

        var result = tree.TrySearch(
            "key03"u8.ToArray(),
            SearchOperator.UpperBound,
            out _,
            out var pos,
            out var nextPageNumber);

        Assert.That(result, Is.False);
        Assert.That(nextPageNumber.HasValue, Is.True);

        await tree.PageCache.LoadAsync(nextPageNumber!.Value);

        var pageNumber = nextPageNumber.Value;

        result = tree.TrySearch(
            pageNumber,
            "key03"u8.ToArray(),
            SearchOperator.UpperBound,
            out _,
            out pos,
            out nextPageNumber);

        Assert.That(result, Is.True);
        Assert.That(nextPageNumber.HasValue, Is.False);

        Assert.That(tree.PageCache.TryGet(pageNumber, out var page), Is.True);

        var header = NodeHeader.Parse(page.Memory.Span);
        var leafNode = new LeafNodeReader(page.Memory.Span, header.EntryCount);

        leafNode.GetAt(pos, out var pageOffset, out var keyLength, out var valueLength);
        var key = page.Memory.Span.Slice(pageOffset, keyLength);
        var value = page.Memory.Span.Slice(pageOffset + keyLength, valueLength);
        Assert.That(key.SequenceEqual("key04"u8), Is.True);
        Assert.That(value.SequenceEqual("value04"u8), Is.True);

        page.Release();
    }

    [Test]
    public async Task Get_SingleLeaf()
    {
        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key1"u8.ToArray(), "value1"u8.ToArray() },
                { "key2"u8.ToArray(), "value2"u8.ToArray() },
                { "key3"u8.ToArray(), "value3"u8.ToArray() },
                { "key4"u8.ToArray(), "value4"u8.ToArray() },
                { "key5"u8.ToArray(), "value5"u8.ToArray() },
            }, 128);

        using var result1 = await tree.GetAsync("key4"u8.ToArray());
        Assert.That(result1.HasValue, Is.True);
        Assert.That(result1.Value.Span.SequenceEqual("value4"u8), Is.True);

        using var result2 = await tree.GetAsync("aaaaa"u8.ToArray());
        Assert.That(result2.HasValue, Is.False);
    }

    [Test]
    public async Task Get_MultipleNode()
    {
        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key1"u8.ToArray(), "value1"u8.ToArray() },
                { "key2"u8.ToArray(), "value2"u8.ToArray() },
                { "key3"u8.ToArray(), "value3"u8.ToArray() },
                { "key4"u8.ToArray(), "value4"u8.ToArray() },
                { "key5"u8.ToArray(), "value5"u8.ToArray() },
                { "key6"u8.ToArray(), "value6"u8.ToArray() },
            }, 128);

        using var result1 = await tree.GetAsync("key3"u8.ToArray());
        Assert.That(result1.HasValue, Is.True);
        Assert.That(result1.Value.Span.SequenceEqual("value3"u8), Is.True);

        using var result2 = await tree.GetAsync("aaaaa"u8.ToArray());
        Assert.That(result2.HasValue, Is.False);
    }

    [Test]
    public async Task GetRange_NotFound()
    {
        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key1"u8.ToArray(), "value1"u8.ToArray() },
                { "key2"u8.ToArray(), "value2"u8.ToArray() },
                { "key3"u8.ToArray(), "value3"u8.ToArray() },
                { "key5"u8.ToArray(), "value5"u8.ToArray() },
                { "key7"u8.ToArray(), "value7"u8.ToArray() },
                { "key8"u8.ToArray(), "value8"u8.ToArray() },
                { "key9"u8.ToArray(), "value9"u8.ToArray() },
            }, 128);

        // > key9
        using var result1 = await tree.GetRangeAsync(
            "key9"u8.ToArray().AsMemory(),
            KeyRange.Unbound,
            startKeyExclusive: true,
            endKeyExclusive: false,
            SortOrder.Ascending);

        Assert.That(result1.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task GetRange_StartInclusiveEndInclusive()
    {
        var table = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key01"u8.ToArray(), "value01"u8.ToArray() },
                { "key02"u8.ToArray(), "value02"u8.ToArray() },
                { "key03"u8.ToArray(), "value03"u8.ToArray() },
                { "key05"u8.ToArray(), "value05"u8.ToArray() },
                { "key07"u8.ToArray(), "value07"u8.ToArray() },
                { "key08"u8.ToArray(), "value08"u8.ToArray() },
                { "key09"u8.ToArray(), "value09"u8.ToArray() },
                { "key10"u8.ToArray(), "value10"u8.ToArray() },
                { "key11"u8.ToArray(), "value11"u8.ToArray() },
            }, 128);

        using var result1 = await table.GetRangeAsync(
            "key03"u8.ToArray().AsMemory(),
            "key09"u8.ToArray().AsMemory(),
            startKeyExclusive: false,
            endKeyExclusive: false,
            SortOrder.Ascending);

        Assert.That(result1.Count, Is.EqualTo(5));
    }

    [Test]
    public async Task GetRange_Descending_BothBounds()
    {
        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key01"u8.ToArray(), "value01"u8.ToArray() },
                { "key02"u8.ToArray(), "value02"u8.ToArray() },
                { "key03"u8.ToArray(), "value03"u8.ToArray() },
                { "key05"u8.ToArray(), "value05"u8.ToArray() },
                { "key07"u8.ToArray(), "value07"u8.ToArray() },
                { "key08"u8.ToArray(), "value08"u8.ToArray() },
                { "key09"u8.ToArray(), "value09"u8.ToArray() },
                { "key10"u8.ToArray(), "value10"u8.ToArray() },
                { "key11"u8.ToArray(), "value11"u8.ToArray() },
            }, 128);

        // ascending result: key03, key05, key07, key08, key09 (5 entries)
        using var ascResult = await tree.GetRangeAsync(
            "key03"u8.ToArray().AsMemory(),
            "key09"u8.ToArray().AsMemory(),
            startKeyExclusive: false,
            endKeyExclusive: false,
            SortOrder.Ascending);

        using var descResult = await tree.GetRangeAsync(
            "key03"u8.ToArray().AsMemory(),
            "key09"u8.ToArray().AsMemory(),
            startKeyExclusive: false,
            endKeyExclusive: false,
            SortOrder.Descending);

        Assert.That(descResult.Count, Is.EqualTo(ascResult.Count));
        // verify reverse order
        for (var i = 0; i < descResult.Count; i++)
        {
            Assert.That(
                descResult[i].Span.SequenceEqual(ascResult[ascResult.Count - 1 - i].Span),
                Is.True,
                $"Mismatch at index {i}");
        }
    }

    [Test]
    public async Task GetRange_Descending_StartBoundOnly()
    {
        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key01"u8.ToArray(), "value01"u8.ToArray() },
                { "key02"u8.ToArray(), "value02"u8.ToArray() },
                { "key03"u8.ToArray(), "value03"u8.ToArray() },
                { "key05"u8.ToArray(), "value05"u8.ToArray() },
                { "key07"u8.ToArray(), "value07"u8.ToArray() },
            }, 128);

        // >= key03 descending => key07, key05, key03
        using var result = await tree.GetRangeAsync(
            "key03"u8.ToArray().AsMemory(),
            KeyRange.Unbound,
            startKeyExclusive: false,
            endKeyExclusive: false,
            SortOrder.Descending);

        Assert.That(result.Count, Is.EqualTo(3));
        Assert.That(result[0].Span.SequenceEqual("value07"u8), Is.True);
        Assert.That(result[1].Span.SequenceEqual("value05"u8), Is.True);
        Assert.That(result[2].Span.SequenceEqual("value03"u8), Is.True);
    }

    [Test]
    public async Task GetRange_Descending_EndBoundOnly()
    {
        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key01"u8.ToArray(), "value01"u8.ToArray() },
                { "key02"u8.ToArray(), "value02"u8.ToArray() },
                { "key03"u8.ToArray(), "value03"u8.ToArray() },
                { "key05"u8.ToArray(), "value05"u8.ToArray() },
                { "key07"u8.ToArray(), "value07"u8.ToArray() },
            }, 128);

        // <= key03 descending => key03, key02, key01
        using var result = await tree.GetRangeAsync(
            KeyRange.Unbound,
            "key03"u8.ToArray().AsMemory(),
            startKeyExclusive: false,
            endKeyExclusive: false,
            SortOrder.Descending);

        Assert.That(result.Count, Is.EqualTo(3));
        Assert.That(result[0].Span.SequenceEqual("value03"u8), Is.True);
        Assert.That(result[1].Span.SequenceEqual("value02"u8), Is.True);
        Assert.That(result[2].Span.SequenceEqual("value01"u8), Is.True);
    }

    [Test]
    public async Task GetRange_Descending_Exclusive()
    {
        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key01"u8.ToArray(), "value01"u8.ToArray() },
                { "key02"u8.ToArray(), "value02"u8.ToArray() },
                { "key03"u8.ToArray(), "value03"u8.ToArray() },
                { "key05"u8.ToArray(), "value05"u8.ToArray() },
                { "key07"u8.ToArray(), "value07"u8.ToArray() },
            }, 128);

        // > key01 AND < key07 descending => key05, key03, key02
        using var result = await tree.GetRangeAsync(
            "key01"u8.ToArray().AsMemory(),
            "key07"u8.ToArray().AsMemory(),
            startKeyExclusive: true,
            endKeyExclusive: true,
            SortOrder.Descending);

        Assert.That(result.Count, Is.EqualTo(3));
        Assert.That(result[0].Span.SequenceEqual("value05"u8), Is.True);
        Assert.That(result[1].Span.SequenceEqual("value03"u8), Is.True);
        Assert.That(result[2].Span.SequenceEqual("value02"u8), Is.True);
    }

    [Test]
    public async Task GetRange_Descending_FullRange()
    {
        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key1"u8.ToArray(), "value1"u8.ToArray() },
                { "key2"u8.ToArray(), "value2"u8.ToArray() },
                { "key3"u8.ToArray(), "value3"u8.ToArray() },
            }, 128);

        using var result = await tree.GetRangeAsync(
            KeyRange.Unbound,
            KeyRange.Unbound,
            startKeyExclusive: false,
            endKeyExclusive: false,
            SortOrder.Descending);

        Assert.That(result.Count, Is.EqualTo(3));
        Assert.That(result[0].Span.SequenceEqual("value3"u8), Is.True);
        Assert.That(result[1].Span.SequenceEqual("value2"u8), Is.True);
        Assert.That(result[2].Span.SequenceEqual("value1"u8), Is.True);
    }

    [Test]
    public async Task GetRange_Descending_NotFound()
    {
        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key1"u8.ToArray(), "value1"u8.ToArray() },
                { "key2"u8.ToArray(), "value2"u8.ToArray() },
                { "key3"u8.ToArray(), "value3"u8.ToArray() },
            }, 128);

        // range where nothing matches: > key3
        using var result = await tree.GetRangeAsync(
            "key4"u8.ToArray().AsMemory(),
            KeyRange.Unbound,
            startKeyExclusive: false,
            endKeyExclusive: false,
            SortOrder.Descending);

        Assert.That(result.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task GetRange_Descending_MultiplePages()
    {
        var entries = new UniqueKeyValueList(KeyEncoding.Ascii);
        for (var i = 0; i < 20; i++)
        {
            entries.Add(
                System.Text.Encoding.ASCII.GetBytes($"key{i:D2}"),
                System.Text.Encoding.ASCII.GetBytes($"val{i:D2}"));
        }

        var tree = await TestHelper.BuildTreeAsync(entries, 128);

        // ascending full range
        using var ascResult = await tree.GetRangeAsync(
            KeyRange.Unbound,
            KeyRange.Unbound,
            sortOrder: SortOrder.Ascending);

        // descending full range
        using var descResult = await tree.GetRangeAsync(
            KeyRange.Unbound,
            KeyRange.Unbound,
            sortOrder: SortOrder.Descending);

        Assert.That(descResult.Count, Is.EqualTo(ascResult.Count));
        Assert.That(descResult.Count, Is.EqualTo(20));
        for (var i = 0; i < descResult.Count; i++)
        {
            Assert.That(
                descResult[i].Span.SequenceEqual(ascResult[ascResult.Count - 1 - i].Span),
                Is.True,
                $"Mismatch at index {i}");
        }
    }
}
