using System;
using System.Text;
using System.Threading.Tasks;

namespace DryDB.Tests;

/// <summary>
/// Exercises the Eytzinger digest layout (format 1.2, DatabaseBuilder.EytzingerDigests):
/// the branch-free descent over the padded complete tree, rank-to-entry resolution,
/// digest-collision runs, and the bound operators used by range queries.
/// </summary>
[TestFixture]
public class EytzingerDigestsTest
{
    static Task<ReadOnlyTable> BuildAsync(IKeyEncoding encoding, Action<TableBuilder> configure) =>
        TestHelper.BuildTableAsync(
            encoding,
            databaseConfigure: builder => builder.EytzingerDigests = true,
            tableConfigure: configure).AsTask();

    [Test]
    public async Task Get_Int64Keys_LargeNodes()
    {
        // Default 4KB pages hold >100 entries per node; entry counts land between
        // powers of two, so the complete tree carries MaxValue padding.
        var table = await BuildAsync(KeyEncoding.Int64LittleEndian, builder =>
        {
            for (var i = 0L; i < 10_000; i++)
            {
                builder.Append(i * 2, Encoding.ASCII.GetBytes($"value{i:D6}"));
            }
        });

        for (var i = 0L; i < 10_000; i++)
        {
            using var result = table.Get(i * 2);
            Assert.That(result.HasValue, Is.True, $"key {i * 2}");
            Assert.That(
                result.Value.Span.SequenceEqual(Encoding.ASCII.GetBytes($"value{i:D6}")),
                Is.True,
                $"key {i * 2}");
        }

        // Odd keys are absent; the descent lands between entries.
        for (var i = 1L; i < 2000; i += 2)
        {
            using var result = table.Get(i);
            Assert.That(result.HasValue, Is.False, $"key {i}");
        }
    }

    [Test]
    public async Task GetRange_Int64Keys_LargeNodes()
    {
        var table = await BuildAsync(KeyEncoding.Int64LittleEndian, builder =>
        {
            for (var i = 0L; i < 10_000; i++)
            {
                builder.Append(i * 2, Encoding.ASCII.GetBytes($"value{i:D6}"));
            }
        });

        // Bounds on existing keys.
        Assert.That(table.CountRange(200L, 400L, false, false), Is.EqualTo(101));

        // Bounds on absent (odd) keys: exercises the lower/upper-bound miss paths.
        Assert.That(table.CountRange(199L, 401L, false, false), Is.EqualTo(101));
        Assert.That(table.CountRange(201L, 399L, false, false), Is.EqualTo(99));

        using var range = table.GetRange<long>(9_000L, 9_100L);
        Assert.That(range.Count, Is.EqualTo(51));

        using var descending = table.GetRange<long>(100L, 200L, sortOrder: SortOrder.Descending);
        Assert.That(descending.Count, Is.EqualTo(51));
    }

    [Test]
    public async Task Get_AsciiKeys_SharedEightBytePrefix()
    {
        // All keys share the first 8 bytes: every digest collides, so the descent's
        // rank lands at the head of one long run resolved by full comparisons.
        var table = await BuildAsync(KeyEncoding.Ascii, builder =>
        {
            for (var i = 0; i < 300; i++)
            {
                builder.Append(
                    Encoding.ASCII.GetBytes($"AAAAAAAA{i:D4}"),
                    Encoding.ASCII.GetBytes($"value{i:D4}"));
            }
        });

        for (var i = 0; i < 300; i++)
        {
            using var result = table.Get(Encoding.ASCII.GetBytes($"AAAAAAAA{i:D4}"));
            Assert.That(result.HasValue, Is.True, $"key {i}");
            Assert.That(
                result.Value.Span.SequenceEqual(Encoding.ASCII.GetBytes($"value{i:D4}")),
                Is.True,
                $"key {i}");
        }

        using var missing = table.Get("AAAAAAAA9999"u8);
        Assert.That(missing.HasValue, Is.False);

        using var range = table.GetRange("AAAAAAAA0010"u8, "AAAAAAAA0019"u8);
        Assert.That(range.Count, Is.EqualTo(10));

        Assert.That(table.CountRange("AAAAAAAA0000"u8, "AAAAAAAA0299"u8), Is.EqualTo(300));
    }

    [Test]
    public async Task SecondaryIndex_NonUnique()
    {
        var table = await BuildAsync(KeyEncoding.Int64LittleEndian, builder =>
        {
            for (var i = 0L; i < 1000; i++)
            {
                builder.Append(i, Encoding.ASCII.GetBytes($"value{i:D4}"));
            }
            builder.AddSecondaryIndex("mod", isUnique: false, KeyEncoding.Int64LittleEndian,
                (key, _) =>
                {
                    var id = BitConverter.ToInt64(key.Span);
                    return id % 10;
                });
        });

        using var all = table.Index("mod").GetAll(3L);
        Assert.That(all.Count, Is.EqualTo(100));
    }

    [Test]
    public async Task Iterator_SeekAndScan()
    {
        var table = await BuildAsync(KeyEncoding.Int64LittleEndian, builder =>
        {
            for (var i = 0L; i < 1000; i++)
            {
                builder.Append(i * 2, Encoding.ASCII.GetBytes($"value{i:D4}"));
            }
        });

        using var iterator = table.CreateIterator();
        Assert.That(iterator.TrySeek(BitConverter.GetBytes(500L)), Is.True);
        Assert.That(iterator.CurrentValue.Span.SequenceEqual(Encoding.ASCII.GetBytes($"value{250:D4}")), Is.True);

        var count = 1;
        while (iterator.MoveNext()) count++;
        Assert.That(count, Is.EqualTo(750));
    }

    [Test]
    public async Task SmallTable_SingleLeaf()
    {
        // Entry counts of 1..8 cross the complete-tree size boundaries (1, 3, 7, 15).
        for (var n = 1; n <= 8; n++)
        {
            var size = n;
            var table = await BuildAsync(KeyEncoding.Int64LittleEndian, builder =>
            {
                for (var i = 0L; i < size; i++)
                {
                    builder.Append(i, Encoding.ASCII.GetBytes($"v{i}"));
                }
            });

            for (var i = 0L; i < size; i++)
            {
                using var result = table.Get(i);
                Assert.That(result.HasValue, Is.True, $"n={size} key={i}");
            }
            using var miss = table.Get((long)size);
            Assert.That(miss.HasValue, Is.False, $"n={size}");
        }
    }
}
