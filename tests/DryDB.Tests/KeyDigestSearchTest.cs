using System;
using System.Text;
using System.Threading.Tasks;

namespace DryDB.Tests;

/// <summary>
/// Exercises the digest-array search paths: the SIMD window kernel (nodes with more
/// than 32 entries) and digest-collision runs (ascii keys sharing an 8-byte prefix,
/// where every digest in the node ties and the search must resolve bounds with full
/// key comparisons).
/// </summary>
[TestFixture]
public class KeyDigestSearchTest
{
    [Test]
    public async Task Get_AsciiKeys_SharedEightBytePrefix()
    {
        // All keys share the first 8 bytes, so every digest in the tree collides and
        // the whole node forms a single digest run.
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            tableConfigure: builder =>
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

        // Same digest as every stored key, but no exact match.
        using var missing = table.Get("AAAAAAAA9999"u8);
        Assert.That(missing.HasValue, Is.False);

        using var missingShort = table.Get("AAAAAAAA"u8);
        Assert.That(missingShort.HasValue, Is.False);
    }

    [Test]
    public async Task GetRange_AsciiKeys_SharedEightBytePrefix()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            tableConfigure: builder =>
            {
                for (var i = 0; i < 300; i++)
                {
                    builder.Append(
                        Encoding.ASCII.GetBytes($"AAAAAAAA{i:D4}"),
                        Encoding.ASCII.GetBytes($"value{i:D4}"));
                }
            });

        using var range = table.GetRange("AAAAAAAA0010"u8, "AAAAAAAA0019"u8);
        Assert.That(range.Count, Is.EqualTo(10));

        using var exclusive = table.GetRange(
            "AAAAAAAA0010"u8,
            "AAAAAAAA0019"u8,
            startKeyExclusive: true,
            endKeyExclusive: true);
        Assert.That(exclusive.Count, Is.EqualTo(8));

        // Bounds that fall between keys (same digest run, no exact match).
        using var between = table.GetRange("AAAAAAAA0010x"u8, "AAAAAAAA0019x"u8);
        Assert.That(between.Count, Is.EqualTo(9));

        Assert.That(table.CountRange("AAAAAAAA0000"u8, "AAAAAAAA0299"u8), Is.EqualTo(300));
        Assert.That(table.CountRange("AAAAAAAA0290"u8, "AAAAAAAA9999"u8), Is.EqualTo(10));
    }

    [Test]
    public async Task Get_Int64Keys_LargeNodes()
    {
        // Default 4KB pages hold >100 entries per node, which drives the search
        // through the 32-wide SIMD window path.
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Int64LittleEndian,
            tableConfigure: builder =>
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

        // Odd keys are absent; the digest lower bound lands between entries.
        for (var i = 1L; i < 2000; i += 2)
        {
            using var result = table.Get(i);
            Assert.That(result.HasValue, Is.False, $"key {i}");
        }
    }

    [Test]
    public async Task GetRange_Int64Keys_LargeNodes()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Int64LittleEndian,
            tableConfigure: builder =>
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
}
