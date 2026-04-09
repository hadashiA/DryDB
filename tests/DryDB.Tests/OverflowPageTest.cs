using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DryDB.BTree;
using DryDB.Compression;
using DryDB.Internal;

namespace DryDB.Tests;

[TestFixture]
public class OverflowPageTest
{
    [Test]
    public async Task Get_OverflowValue()
    {
        // pageSize=128, key=4 bytes. Value of 100 bytes won't fit inline (~92 byte limit).
        var value = new byte[100];
        Random.Shared.NextBytes(value);

        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key1"u8.ToArray(), value },
            }, 128);

        using var result = tree.Get("key1"u8);
        Assert.That(result.HasValue, Is.True);
        Assert.That(result.Value.Span.SequenceEqual(value), Is.True);
    }

    [Test]
    public async Task Get_MixedInlineAndOverflow()
    {
        var smallValue = "small"u8.ToArray();
        var largeValue = new byte[100];
        Random.Shared.NextBytes(largeValue);

        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key1"u8.ToArray(), smallValue },
                { "key2"u8.ToArray(), largeValue },
                { "key3"u8.ToArray(), smallValue },
            }, 128);

        using var r1 = tree.Get("key1"u8);
        Assert.That(r1.HasValue, Is.True);
        Assert.That(r1.Value.Span.SequenceEqual(smallValue), Is.True);

        using var r2 = tree.Get("key2"u8);
        Assert.That(r2.HasValue, Is.True);
        Assert.That(r2.Value.Span.SequenceEqual(largeValue), Is.True);

        using var r3 = tree.Get("key3"u8);
        Assert.That(r3.HasValue, Is.True);
        Assert.That(r3.Value.Span.SequenceEqual(smallValue), Is.True);
    }

    [Test]
    public async Task GetAsync_OverflowValue()
    {
        var value = new byte[100];
        Random.Shared.NextBytes(value);

        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key1"u8.ToArray(), value },
            }, 128);

        using var result = await tree.GetAsync("key1"u8.ToArray());
        Assert.That(result.HasValue, Is.True);
        Assert.That(result.Value.Span.SequenceEqual(value), Is.True);
    }

    [Test]
    public async Task GetRange_MixedInlineAndOverflow()
    {
        var smallValue = "val"u8.ToArray();
        var largeValue = new byte[100];
        Random.Shared.NextBytes(largeValue);

        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key1"u8.ToArray(), smallValue },
                { "key2"u8.ToArray(), largeValue },
                { "key3"u8.ToArray(), smallValue },
                { "key4"u8.ToArray(), largeValue },
                { "key5"u8.ToArray(), smallValue },
            }, 128);

        using var result = tree.GetRange(
            "key1"u8,
            "key5"u8,
            startKeyExclusive: false,
            endKeyExclusive: false,
            SortOrder.Ascending);

        Assert.That(result.Count, Is.EqualTo(5));
        Assert.That(result[0].Span.SequenceEqual(smallValue), Is.True);
        Assert.That(result[1].Span.SequenceEqual(largeValue), Is.True);
        Assert.That(result[2].Span.SequenceEqual(smallValue), Is.True);
        Assert.That(result[3].Span.SequenceEqual(largeValue), Is.True);
        Assert.That(result[4].Span.SequenceEqual(smallValue), Is.True);
    }

    [Test]
    public async Task GetRangeAsync_MixedOverflow()
    {
        var smallValue = "val"u8.ToArray();
        var largeValue = new byte[100];
        Random.Shared.NextBytes(largeValue);

        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key1"u8.ToArray(), smallValue },
                { "key2"u8.ToArray(), largeValue },
                { "key3"u8.ToArray(), smallValue },
            }, 128);

        using var result = await tree.GetRangeAsync(
            "key1"u8.ToArray().AsMemory(),
            "key3"u8.ToArray().AsMemory(),
            startKeyExclusive: false,
            endKeyExclusive: false,
            SortOrder.Ascending);

        Assert.That(result.Count, Is.EqualTo(3));
        Assert.That(result[0].Span.SequenceEqual(smallValue), Is.True);
        Assert.That(result[1].Span.SequenceEqual(largeValue), Is.True);
        Assert.That(result[2].Span.SequenceEqual(smallValue), Is.True);
    }

    [Test]
    public async Task GetRange_Descending_WithOverflow()
    {
        var smallValue = "val"u8.ToArray();
        var largeValue = new byte[100];
        Random.Shared.NextBytes(largeValue);

        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key1"u8.ToArray(), smallValue },
                { "key2"u8.ToArray(), largeValue },
                { "key3"u8.ToArray(), smallValue },
            }, 128);

        using var result = tree.GetRange(
            "key1"u8,
            "key3"u8,
            startKeyExclusive: false,
            endKeyExclusive: false,
            SortOrder.Descending);

        Assert.That(result.Count, Is.EqualTo(3));
        Assert.That(result[0].Span.SequenceEqual(smallValue), Is.True);   // key3
        Assert.That(result[1].Span.SequenceEqual(largeValue), Is.True);   // key2
        Assert.That(result[2].Span.SequenceEqual(smallValue), Is.True);   // key1
    }

    [Test]
    public async Task RangeIterator_Forward_WithOverflow()
    {
        var smallValue = "val"u8.ToArray();
        var largeValue = new byte[100];
        Random.Shared.NextBytes(largeValue);

        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key1"u8.ToArray(), smallValue },
                { "key2"u8.ToArray(), largeValue },
                { "key3"u8.ToArray(), smallValue },
            }, 128);

        var iterator = tree.CreateIterator(IteratorDirection.Forward);

        Assert.That(iterator.MoveNext(), Is.True);
        Assert.That(iterator.Current.Span.SequenceEqual(smallValue), Is.True);

        Assert.That(iterator.MoveNext(), Is.True);
        Assert.That(iterator.Current.Span.SequenceEqual(largeValue), Is.True);

        Assert.That(iterator.MoveNext(), Is.True);
        Assert.That(iterator.Current.Span.SequenceEqual(smallValue), Is.True);

        Assert.That(iterator.MoveNext(), Is.False);
        iterator.Dispose();
    }

    [Test]
    public async Task RangeIterator_Backward_WithOverflow()
    {
        var smallValue = "val"u8.ToArray();
        var largeValue = new byte[100];
        Random.Shared.NextBytes(largeValue);

        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key1"u8.ToArray(), smallValue },
                { "key2"u8.ToArray(), largeValue },
                { "key3"u8.ToArray(), smallValue },
            }, 128);

        var iterator = tree.CreateIterator(IteratorDirection.Backward);

        Assert.That(iterator.MoveNext(), Is.True);
        Assert.That(iterator.Current.Span.SequenceEqual(smallValue), Is.True);  // key3

        Assert.That(iterator.MoveNext(), Is.True);
        Assert.That(iterator.Current.Span.SequenceEqual(largeValue), Is.True);  // key2

        Assert.That(iterator.MoveNext(), Is.True);
        Assert.That(iterator.Current.Span.SequenceEqual(smallValue), Is.True);  // key1

        Assert.That(iterator.MoveNext(), Is.False);
        iterator.Dispose();
    }

    [Test]
    public async Task RangeIterator_Seek_OverflowValue()
    {
        var smallValue = "val"u8.ToArray();
        var largeValue = new byte[100];
        Random.Shared.NextBytes(largeValue);

        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key1"u8.ToArray(), smallValue },
                { "key2"u8.ToArray(), largeValue },
                { "key3"u8.ToArray(), smallValue },
            }, 128);

        var iterator = tree.CreateIterator();
        Assert.That(iterator.TrySeek("key2"u8), Is.True);
        Assert.That(iterator.Current.Span.SequenceEqual(largeValue), Is.True);

        Assert.That(iterator.MoveNext(), Is.True);
        Assert.That(iterator.Current.Span.SequenceEqual(smallValue), Is.True);  // key3

        Assert.That(iterator.MoveNext(), Is.False);
        iterator.Dispose();
    }

    [Test]
    public async Task Get_LargeValue_1MB()
    {
        var value = new byte[1024 * 1024]; // 1MB
        Random.Shared.NextBytes(value);

        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder => builder.PageSize = 4096,
            tableConfigure: builder =>
            {
                builder.Append("key1"u8.ToArray(), value);
            });

        using var result = await table.GetAsync("key1"u8.ToArray());
        Assert.That(result.HasValue, Is.True);
        Assert.That(result.Value.Span.SequenceEqual(value), Is.True);
    }

    [Test]
    public async Task GetRange_MultipleOverflowValues()
    {
        var values = new byte[5][];
        for (var i = 0; i < 5; i++)
        {
            values[i] = new byte[100];
            Random.Shared.NextBytes(values[i]);
        }

        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "key1"u8.ToArray(), values[0] },
                { "key2"u8.ToArray(), values[1] },
                { "key3"u8.ToArray(), values[2] },
                { "key4"u8.ToArray(), values[3] },
                { "key5"u8.ToArray(), values[4] },
            }, 128);

        using var result = tree.GetRange(
            "key1"u8,
            "key5"u8,
            startKeyExclusive: false,
            endKeyExclusive: false,
            SortOrder.Ascending);

        Assert.That(result.Count, Is.EqualTo(5));
        for (var i = 0; i < 5; i++)
        {
            Assert.That(result[i].Span.SequenceEqual(values[i]), Is.True, $"Mismatch at index {i}");
        }
    }

    [Test]
    public async Task Get_OverflowWithCompression()
    {
        var value = new byte[200];
        Random.Shared.NextBytes(value);

        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder =>
            {
                builder.PageSize = 128;
                builder.AddPageFilter(x => x.AddZstandardCompression());
            },
            tableConfigure: builder =>
            {
                builder.Append("key1"u8.ToArray(), value);
                builder.Append("key2"u8.ToArray(), "small"u8.ToArray());
            });

        using var r1 = await table.GetAsync("key1"u8.ToArray());
        Assert.That(r1.HasValue, Is.True);
        Assert.That(r1.Value.Span.SequenceEqual(value), Is.True);

        using var r2 = await table.GetAsync("key2"u8.ToArray());
        Assert.That(r2.HasValue, Is.True);
        Assert.That(r2.Value.Span.SequenceEqual("small"u8), Is.True);
    }

    [Test]
    public async Task BoundaryValue_InlineMax()
    {
        // With pageSize=128, key="k" (1 byte):
        // PageHeaderSize(28) + 1*meta(8) + keyLen(1) + valueLen = 128
        // valueLen = 128 - 28 - 8 - 1 = 91 => inline max
        var inlineValue = new byte[91];
        Random.Shared.NextBytes(inlineValue);

        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "k"u8.ToArray(), inlineValue },
            }, 128);

        using var result = tree.Get("k"u8);
        Assert.That(result.HasValue, Is.True);
        Assert.That(result.Value.Span.SequenceEqual(inlineValue), Is.True);
    }

    [Test]
    public async Task BoundaryValue_OverflowMin()
    {
        // With pageSize=128, key="k" (1 byte):
        // inline max = 91 bytes, so 92+ bytes triggers overflow
        var overflowValue = new byte[92];
        Random.Shared.NextBytes(overflowValue);

        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "k"u8.ToArray(), overflowValue },
            }, 128);

        using var result = tree.Get("k"u8);
        Assert.That(result.HasValue, Is.True);
        Assert.That(result.Value.Span.SequenceEqual(overflowValue), Is.True);
    }

    [Test]
    public async Task GetMinValue_Overflow()
    {
        var largeValue = new byte[100];
        Random.Shared.NextBytes(largeValue);

        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "aaa"u8.ToArray(), largeValue },
                { "bbb"u8.ToArray(), "small"u8.ToArray() },
            }, 128);

        using var result = tree.GetMinValue();
        Assert.That(result.HasValue, Is.True);
        Assert.That(result.Value.Span.SequenceEqual(largeValue), Is.True);
    }

    [Test]
    public async Task GetMaxValue_Overflow()
    {
        var largeValue = new byte[100];
        Random.Shared.NextBytes(largeValue);

        var tree = await TestHelper.BuildTreeAsync(
            new UniqueKeyValueList(KeyEncoding.Ascii)
            {
                { "aaa"u8.ToArray(), "small"u8.ToArray() },
                { "bbb"u8.ToArray(), largeValue },
            }, 128);

        using var result = tree.GetMaxValue();
        Assert.That(result.HasValue, Is.True);
        Assert.That(result.Value.Span.SequenceEqual(largeValue), Is.True);
    }
}
