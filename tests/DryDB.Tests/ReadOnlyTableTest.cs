using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using DryDB.Compression;
using DryDB.UlidKey;

namespace DryDB.Tests;

[TestFixture]
public class ReadOnlyTableTest
{
    [Test]
    public async Task Get_ByteKey()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                for (var i = 0; i < 1000; i++)
                {
                    builder.Append(
                        Encoding.ASCII.GetBytes($"key{i:D5}"),
                        Encoding.ASCII.GetBytes($"value{i:D5}"));
                }
            });

        using var result1 = await table.GetAsync("key00123"u8.ToArray());
        Assert.That(result1.HasValue, Is.True);
        Assert.That(result1.Value.Span.SequenceEqual("value00123"u8), Is.True);

        using var result2 = await table.GetAsync("aaaaa"u8.ToArray());
        Assert.That(result2.HasValue, Is.False);
    }

    [Test]
    public async Task Get_TypedKey()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                for (var i = 0; i < 100; i++)
                {
                    builder.Append($"key{i:D3}", Encoding.ASCII.GetBytes($"value{i:D3}"));
                }
            });

        using var result1 = table.Get("key050");
        Assert.That(result1.HasValue, Is.True);
        Assert.That(result1.Value.Span.SequenceEqual("value050"u8), Is.True);

        using var result2 = table.Get("notfound");
        Assert.That(result2.HasValue, Is.False);
    }

    [Test]
    public async Task Get_Int64Key()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Int64LittleEndian,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                for (var i = 0; i < 100; i++)
                {
                    builder.Append((long)i, Encoding.ASCII.GetBytes($"value{i:D3}"));
                }
            });

        using var result1 = await table.GetAsync(50L);
        Assert.That(result1.HasValue, Is.True);
        Assert.That(result1.Value.Span.SequenceEqual("value050"u8), Is.True);

        using var result2 = await table.GetAsync(999L);
        Assert.That(result2.HasValue, Is.False);
    }

    [Test]
    public async Task Get_UlidKey()
    {
        var keys = new List<Ulid>();

        var table = await TestHelper.BuildTableAsync(
            UlidKeyEncoding.Instance,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                for (var i = 0; i < 100; i++)
                {
                    var key = Ulid.NewUlid();
                    builder.Append(key, Encoding.ASCII.GetBytes($"value{i:D3}"));
                    keys.Add(key);
                }
            });

        using var result1 = await table.GetAsync(keys[50]);

        Assert.That(result1.HasValue, Is.True);
        Assert.That(result1.Value.Span.SequenceEqual("value050"u8), Is.True);

        using var result2 = await table.GetAsync(Ulid.NewUlid());
        Assert.That(result2.HasValue, Is.False);
    }

    [Test]
    public async Task Get_Uuidv7Key()
    {
        var keys = new List<Guid>();

        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Uuidv7,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                for (var i = 0; i < 100; i++)
                {
                    var key = Guid.CreateVersion7();
                    builder.Append(key, Encoding.ASCII.GetBytes($"value{i:D3}"));
                    keys.Add(key);
                }
            });

        using var result1 = await table.GetAsync(keys[50]);
        Assert.That(result1.HasValue, Is.True);
        Assert.That(result1.Value.Span.SequenceEqual("value050"u8), Is.True);

        using var result2 = await table.GetAsync(Guid.CreateVersion7());
        Assert.That(result2.HasValue, Is.False);
    }

    [Test]
    public async Task GetCompressed()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder =>
            {
                builder.PageSize = 128;
                builder.AddPageFilter(x => x.AddZstandardCompression());
            },
            tableConfigure: builder =>
            {
                for (var i = 0; i < 10; i++)
                {
                    builder.Append(
                        Encoding.ASCII.GetBytes($"k{i:D2}"),
                        Encoding.ASCII.GetBytes($"v{i:D2}"));
                }
            });

        using var result1 = await table.GetAsync("k02"u8.ToArray());
        Assert.That(result1.HasValue, Is.True);
        Assert.That(result1.Value.Span.SequenceEqual("v02"u8), Is.True);

        using var result2 = await table.GetAsync("aaaaa"u8.ToArray());
        Assert.That(result2.HasValue, Is.False);
    }

    [Test]
    public async Task GetRange_Between()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                for (var i = 0; i < 100; i++)
                {
                    builder.Append(
                        Encoding.ASCII.GetBytes($"key{i:D3}"),
                        Encoding.ASCII.GetBytes($"value{i:D3}"));
                }
            });

        // key050 ~ key060 (inclusive)
        using var result1 = await table.GetRangeAsync(
            "key050"u8.ToArray(),
            "key060"u8.ToArray());
        Assert.That(result1.Count, Is.EqualTo(11)); // 050, 051, ..., 060
    }

    [Test]
    public async Task GetRange_GreaterThan()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                for (var i = 0; i < 10; i++)
                {
                    builder.Append(
                        Encoding.ASCII.GetBytes($"key{i:D2}"),
                        Encoding.ASCII.GetBytes($"value{i:D2}"));
                }
            });

        // > key07
        using var result = await table.GetRangeAsync(
            "key07"u8.ToArray(),
            KeyRange.Unbound,
            startKeyExclusive: true,
            endKeyExclusive: false,
            SortOrder.Ascending);
        Assert.That(result.Count, Is.EqualTo(2)); // key08, key09
    }

    [Test]
    public async Task GetRange_LessThan()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                for (var i = 0; i < 10; i++)
                {
                    builder.Append(
                        Encoding.ASCII.GetBytes($"key{i:D2}"),
                        Encoding.ASCII.GetBytes($"value{i:D2}"));
                }
            });

        // < key03
        using var result = await table.GetRangeAsync(
            KeyRange.Unbound,
            "key03"u8.ToArray(),
            startKeyExclusive: false,
            endKeyExclusive: true,
            SortOrder.Ascending);
        Assert.That(result.Count, Is.EqualTo(3)); // key00, key01, key02
    }

    [Test]
    public async Task CountRange()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                for (var i = 0; i < 100; i++)
                {
                    builder.Append(
                        Encoding.ASCII.GetBytes($"key{i:D3}"),
                        Encoding.ASCII.GetBytes($"value{i:D3}"));
                }
            });

        var count = await table.CountRangeAsync(
            "key020"u8.ToArray(),
            "key030"u8.ToArray(),
            startKeyExclusive: false,
            endKeyExclusive: false);
        Assert.That(count, Is.EqualTo(11)); // 020, 021, ..., 030
    }

    [Test]
    public async Task GetRangeWithPrefix_Basic()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                builder.Append("abc001"u8.ToArray(), "v1"u8.ToArray());
                builder.Append("abc002"u8.ToArray(), "v2"u8.ToArray());
                builder.Append("abc003"u8.ToArray(), "v3"u8.ToArray());
                builder.Append("abd001"u8.ToArray(), "v4"u8.ToArray());
                builder.Append("xyz001"u8.ToArray(), "v5"u8.ToArray());
            });

        using var result = table.GetRangeWithPrefix("abc"u8);
        Assert.That(result.Count, Is.EqualTo(3));
    }

    [Test]
    public async Task GetRangeWithPrefix_EmptyPrefix()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                builder.Append("key1"u8.ToArray(), "v1"u8.ToArray());
            });

        using var result = table.GetRangeWithPrefix(ReadOnlySpan<byte>.Empty);
        Assert.That(result.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task GetRangeWithPrefix_NoMatch()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                builder.Append("abc001"u8.ToArray(), "v1"u8.ToArray());
                builder.Append("abc002"u8.ToArray(), "v2"u8.ToArray());
            });

        using var result = table.GetRangeWithPrefix("zzz"u8);
        Assert.That(result.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task GetRangeWithPrefix_TrailingFF()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                builder.Append(new byte[] { 0x61, 0xFF, 0x01 }, "v1"u8.ToArray());
                builder.Append(new byte[] { 0x61, 0xFF, 0x02 }, "v2"u8.ToArray());
                builder.Append(new byte[] { 0x62, 0x00, 0x01 }, "v3"u8.ToArray());
            });

        // Prefix [0x61, 0xFF] — trailing 0xFF stripped, endKey becomes [0x62], excludes v3
        using var result = table.GetRangeWithPrefix([0x61, 0xFF]);
        Assert.That(result.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task GetRangeWithPrefix_AllFF()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                builder.Append(new byte[] { 0xFF, 0xFF, 0x01 }, "v1"u8.ToArray());
                builder.Append(new byte[] { 0xFF, 0xFF, 0x02 }, "v2"u8.ToArray());
            });

        // All 0xFF prefix — should scan from prefix to end
        using var result = table.GetRangeWithPrefix([0xFF, 0xFF]);
        Assert.That(result.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task GetRangeWithPrefix_TypedKey()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                for (var i = 0; i < 100; i++)
                {
                    builder.Append($"key{i:D3}", Encoding.ASCII.GetBytes($"value{i:D3}"));
                }
            });

        using var result = table.GetRangeWithPrefix("key01");
        // key010..key019 = 10 entries
        Assert.That(result.Count, Is.EqualTo(10));
    }

    [Test]
    public async Task GetRangeWithPrefixAsync_Basic()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                builder.Append("abc001"u8.ToArray(), "v1"u8.ToArray());
                builder.Append("abc002"u8.ToArray(), "v2"u8.ToArray());
                builder.Append("abc003"u8.ToArray(), "v3"u8.ToArray());
                builder.Append("abd001"u8.ToArray(), "v4"u8.ToArray());
            });

        using var result = await table.GetRangeWithPrefixAsync("abc"u8.ToArray());
        Assert.That(result.Count, Is.EqualTo(3));
    }

    [Test]
    public async Task GetRangeWithPrefixAsync_TypedKey()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                for (var i = 0; i < 100; i++)
                {
                    builder.Append($"key{i:D3}", Encoding.ASCII.GetBytes($"value{i:D3}"));
                }
            });

        using var result = await table.GetRangeWithPrefixAsync("key05");
        // key050..key059 = 10 entries
        Assert.That(result.Count, Is.EqualTo(10));
    }

    [Test]
    public async Task GetRange_TypedKey()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Int64LittleEndian,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                for (var i = 0; i < 100; i++)
                {
                    builder.Append((long)i, Encoding.ASCII.GetBytes($"value{i:D3}"));
                }
            });

        using var result = table.GetRange(10L, 20L,
            startKeyExclusive: false,
            endKeyExclusive: false,
            SortOrder.Ascending);
        Assert.That(result.Count, Is.EqualTo(11)); // 10, 11, ..., 20
    }

    [Test]
    public async Task GetRange_Descending_Between()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                for (var i = 0; i < 100; i++)
                {
                    builder.Append(
                        Encoding.ASCII.GetBytes($"key{i:D3}"),
                        Encoding.ASCII.GetBytes($"value{i:D3}"));
                }
            });

        // key050 ~ key060 descending (inclusive)
        using var ascResult = await table.GetRangeAsync(
            "key050"u8.ToArray(),
            "key060"u8.ToArray(),
            sortOrder: SortOrder.Ascending);

        using var descResult = await table.GetRangeAsync(
            "key050"u8.ToArray(),
            "key060"u8.ToArray(),
            sortOrder: SortOrder.Descending);

        Assert.That(descResult.Count, Is.EqualTo(11)); // 050..060
        for (var i = 0; i < descResult.Count; i++)
        {
            Assert.That(
                descResult[i].Span.SequenceEqual(ascResult[ascResult.Count - 1 - i].Span),
                Is.True);
        }
    }

    [Test]
    public async Task GetRange_Descending_GreaterThan()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                for (var i = 0; i < 10; i++)
                {
                    builder.Append(
                        Encoding.ASCII.GetBytes($"key{i:D2}"),
                        Encoding.ASCII.GetBytes($"value{i:D2}"));
                }
            });

        // > key07 descending => key09, key08
        using var result = await table.GetRangeAsync(
            "key07"u8.ToArray(),
            KeyRange.Unbound,
            startKeyExclusive: true,
            endKeyExclusive: false,
            SortOrder.Descending);
        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result[0].Span.SequenceEqual("value09"u8), Is.True);
        Assert.That(result[1].Span.SequenceEqual("value08"u8), Is.True);
    }

    [Test]
    public async Task GetRange_Descending_LessThan()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                for (var i = 0; i < 10; i++)
                {
                    builder.Append(
                        Encoding.ASCII.GetBytes($"key{i:D2}"),
                        Encoding.ASCII.GetBytes($"value{i:D2}"));
                }
            });

        // < key03 descending => key02, key01, key00
        using var result = await table.GetRangeAsync(
            KeyRange.Unbound,
            "key03"u8.ToArray(),
            startKeyExclusive: false,
            endKeyExclusive: true,
            SortOrder.Descending);
        Assert.That(result.Count, Is.EqualTo(3));
        Assert.That(result[0].Span.SequenceEqual("value02"u8), Is.True);
        Assert.That(result[1].Span.SequenceEqual("value01"u8), Is.True);
        Assert.That(result[2].Span.SequenceEqual("value00"u8), Is.True);
    }
}
