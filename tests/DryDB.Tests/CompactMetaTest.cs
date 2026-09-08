using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DryDB.BTree;
using DryDB.Internal;
using DryDB.Storages;

namespace DryDB.Tests;

/// <summary>
/// Format 1.4 layouts: compact entry metadata (derived lengths from a ushort offset
/// array) and key-less pages for exact-digest encodings (the digest array doubles as
/// the key column). See <see cref="NodeFlags"/>.
/// </summary>
[TestFixture]
public class CompactMetaTest
{
    static byte[] I64(long value) => BitConverter.GetBytes(value);

    static async ValueTask<(TreeWalker Walker, byte[] File, PageDirectory Directory, TreeBuildResult Result)> BuildTreeAsync(
        IKeyEncoding encoding,
        KeyValueList keyValues,
        int pageSize,
        bool eytzingerDigests = false)
    {
        var memoryStream = new MemoryStream();
        var pageDirectory = new PageDirectory();
        var buildResult = await TreeBuilder.BuildToAsync(
            memoryStream,
            pageSize,
            keyValues,
            pageDirectory,
            eytzingerDigests: eytzingerDigests);

        var file = memoryStream.ToArray();
        var storage = new InMemoryPageLoader(file.ToArray());
        var pageCache = new PageCache(storage, pageDirectory.Offsets.ToArray(), 8, []);
        return (TreeWalker.Create(buildResult.RootPageNumber, pageCache, encoding), file, pageDirectory, buildResult);
    }

    static NodeHeader ParseNode(byte[] file, PageDirectory directory, PageNumber pageNumber) =>
        NodeHeader.Parse(file.AsSpan((int)directory.Offsets[(int)pageNumber.Value]));

    [Test]
    public async Task Int64_OmittedKeys_FlagsAndLookups()
    {
        var keyValues = new UniqueKeyValueList(KeyEncoding.Int64LittleEndian);
        // Include negative keys: the sign-bit flip must round-trip through the digest.
        for (var i = -500L; i < 500; i++)
        {
            keyValues.Add(I64(i), Encoding.ASCII.GetBytes($"value{i}"));
        }

        var (walker, file, directory, result) = await BuildTreeAsync(
            KeyEncoding.Int64LittleEndian, keyValues, pageSize: 256);

        // Multi-level tree whose root (internal) and leaves both omit keys.
        var rootHeader = ParseNode(file, directory, result.RootPageNumber);
        Assert.That(rootHeader.NodeKind, Is.EqualTo(NodeKind.Internal));
        Assert.That(rootHeader.HasKeyDigests, Is.True);
        Assert.That(rootHeader.HasCompactMeta, Is.True);
        Assert.That(rootHeader.HasOmittedKeys, Is.True);

        var minLeaf = walker.GetMinLeaf();
        Assert.That(minLeaf.HasValue, Is.True);
        var leafHeader = NodeHeader.Parse(minLeaf!.Value.Page.Memory.Span);
        Assert.That(leafHeader.NodeKind, Is.EqualTo(NodeKind.Leaf));
        Assert.That(leafHeader.HasCompactMeta, Is.True);
        Assert.That(leafHeader.HasOmittedKeys, Is.True);
        minLeaf.Value.Page.Release();

        for (var i = -500L; i < 500; i++)
        {
            using var found = walker.Get(I64(i));
            Assert.That(found.HasValue, Is.True, $"key {i}");
            Assert.That(found.Value.Span.SequenceEqual(Encoding.ASCII.GetBytes($"value{i}")), Is.True, $"key {i}");
        }

        using var missing = walker.Get(I64(12345));
        Assert.That(missing.HasValue, Is.False);

        using var range = walker.GetRange(I64(-3), I64(3));
        Assert.That(range.Count, Is.EqualTo(7));
        Assert.That(range[0].Span.SequenceEqual(Encoding.ASCII.GetBytes("value-3")), Is.True);
        Assert.That(range[6].Span.SequenceEqual(Encoding.ASCII.GetBytes("value3")), Is.True);

        Assert.That(walker.CountRange(I64(-499), I64(499), startKeyExclusive: true), Is.EqualTo(998));
    }

    [Test]
    public async Task Int64_OmittedKeys_IteratorReconstructsKeys()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Int64LittleEndian,
            tableConfigure: builder =>
            {
                for (var i = 0L; i < 1000; i++)
                {
                    builder.Append(I64(i * 3), Encoding.ASCII.GetBytes($"value{i * 3}"));
                }
            });

        // Keys are not stored on the page; CurrentKey must reconstruct the original
        // bytes from the digest.
        using var iterator = table.CreateIterator();
        var count = 0L;
        while (iterator.MoveNext())
        {
            var expectedKey = count * 3;
            Assert.That(iterator.CurrentKey.Span.SequenceEqual(I64(expectedKey)), Is.True, $"key {expectedKey}");
            Assert.That(
                iterator.CurrentValue.Span.SequenceEqual(Encoding.ASCII.GetBytes($"value{expectedKey}")),
                Is.True);
            count++;
        }
        Assert.That(count, Is.EqualTo(1000));

        using var seekIterator = table.CreateIterator();
        Assert.That(seekIterator.TrySeek(I64(1000)), Is.False);
        Assert.That(seekIterator.TrySeek(I64(2400)), Is.True);
        Assert.That(seekIterator.CurrentKey.Span.SequenceEqual(I64(2400)), Is.True);
    }

    [Test]
    public async Task Int64_OmittedKeys_OverflowValues()
    {
        var bigValue = new byte[10_000];
        new Random(42).NextBytes(bigValue);

        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Int64LittleEndian,
            tableConfigure: builder =>
            {
                for (var i = 0L; i < 100; i++)
                {
                    builder.Append(I64(i), Encoding.ASCII.GetBytes($"small{i}"));
                }
                builder.Append(I64(100), bigValue); // > pageSize, becomes a blob page
                for (var i = 101L; i < 200; i++)
                {
                    builder.Append(I64(i), Encoding.ASCII.GetBytes($"small{i}"));
                }
            });

        using var big = await table.GetAsync(100);
        Assert.That(big.HasValue, Is.True);
        Assert.That(big.Value.Span.SequenceEqual(bigValue), Is.True);

        using var before = await table.GetAsync(99);
        Assert.That(before.Value.Span.SequenceEqual("small99"u8), Is.True);
        using var after = await table.GetAsync(101);
        Assert.That(after.Value.Span.SequenceEqual("small101"u8), Is.True);

        // The overflow entry must round-trip through range reads as well (the leaf
        // offset carries the overflow flag in bit 15).
        using var range = await table.GetRangeAsync(I64(99), I64(101));
        Assert.That(range.Count, Is.EqualTo(3));
        Assert.That(range[1].Span.SequenceEqual(bigValue), Is.True);
    }

    [Test]
    public async Task Ascii_CompactMeta_KeepsKeys()
    {
        var keyValues = new UniqueKeyValueList(KeyEncoding.Ascii);
        for (var i = 0; i < 1000; i++)
        {
            keyValues.Add(
                Encoding.ASCII.GetBytes($"key{i:D5}"),
                Encoding.ASCII.GetBytes($"value{i:D5}"));
        }

        var (walker, file, directory, result) = await BuildTreeAsync(
            KeyEncoding.Ascii, keyValues, pageSize: 256);

        // ASCII digests are coarse (first 8 bytes), so keys stay on the page.
        var rootHeader = ParseNode(file, directory, result.RootPageNumber);
        Assert.That(rootHeader.HasCompactMeta, Is.True);
        Assert.That(rootHeader.HasOmittedKeys, Is.False);

        for (var i = 0; i < 1000; i++)
        {
            using var found = walker.Get(Encoding.ASCII.GetBytes($"key{i:D5}"));
            Assert.That(found.HasValue, Is.True, $"key {i}");
            Assert.That(found.Value.Span.SequenceEqual(Encoding.ASCII.GetBytes($"value{i:D5}")), Is.True);
        }

        using var missing = walker.Get("key99999"u8.ToArray());
        Assert.That(missing.HasValue, Is.False);

        using var range = walker.GetRange("key00010"u8, "key00012"u8);
        Assert.That(range.Count, Is.EqualTo(3));
    }

    [Test]
    public async Task LargePageSize_FallsBackToClassicMeta()
    {
        var keyValues = new UniqueKeyValueList(KeyEncoding.Int64LittleEndian);
        for (var i = 0L; i < 5000; i++)
        {
            keyValues.Add(I64(i), Encoding.ASCII.GetBytes($"value{i}"));
        }

        // 65536 > MaxCompactPageSize: offsets would not fit in 15 bits, so the
        // classic meta records (and stored keys) are kept.
        var (walker, file, directory, result) = await BuildTreeAsync(
            KeyEncoding.Int64LittleEndian, keyValues, pageSize: 65536);

        var rootHeader = ParseNode(file, directory, result.RootPageNumber);
        Assert.That(rootHeader.HasCompactMeta, Is.False);
        Assert.That(rootHeader.HasOmittedKeys, Is.False);
        Assert.That(rootHeader.HasKeyDigests, Is.True);

        for (var i = 0L; i < 5000; i += 7)
        {
            using var found = walker.Get(I64(i));
            Assert.That(found.HasValue, Is.True, $"key {i}");
            Assert.That(found.Value.Span.SequenceEqual(Encoding.ASCII.GetBytes($"value{i}")), Is.True);
        }
    }

    [Test]
    public async Task Eytzinger_Int64_KeepsKeys()
    {
        var keyValues = new UniqueKeyValueList(KeyEncoding.Int64LittleEndian);
        for (var i = 0L; i < 2000; i++)
        {
            keyValues.Add(I64(i), Encoding.ASCII.GetBytes($"value{i}"));
        }

        // Eytzinger pages scatter the digests out of sorted order, so keys cannot be
        // omitted (enumeration could no longer reconstruct entry i's key); the compact
        // offsets still apply.
        var (walker, file, directory, result) = await BuildTreeAsync(
            KeyEncoding.Int64LittleEndian, keyValues, pageSize: 4096, eytzingerDigests: true);

        var rootHeader = ParseNode(file, directory, result.RootPageNumber);
        Assert.That(rootHeader.HasEytzingerDigests, Is.True);
        Assert.That(rootHeader.HasCompactMeta, Is.True);
        Assert.That(rootHeader.HasOmittedKeys, Is.False);

        for (var i = 0L; i < 2000; i += 3)
        {
            using var found = walker.Get(I64(i));
            Assert.That(found.HasValue, Is.True, $"key {i}");
            Assert.That(found.Value.Span.SequenceEqual(Encoding.ASCII.GetBytes($"value{i}")), Is.True);
        }
    }

    [Test]
    public async Task Int64_OmittedKeys_ShrinksFile()
    {
        static async Task<long> BuildFileAsync(int pageSize)
        {
            var keyValues = new UniqueKeyValueList(KeyEncoding.Int64LittleEndian);
            for (var i = 0L; i < 10_000; i++)
            {
                keyValues.Add(I64(i), I64(i * 2));
            }
            var memoryStream = new MemoryStream();
            await TreeBuilder.BuildToAsync(memoryStream, pageSize, keyValues, new PageDirectory());
            return memoryStream.Length;
        }

        var compact = await BuildFileAsync(4096);
        var classic = await BuildFileAsync(65536);

        // 8B key + 8B value entries: 32B/entry classic vs 18B/entry compact. Page
        // boundaries blur the exact ratio; just require a substantial shrink.
        Assert.That(compact, Is.LessThan(classic * 0.7));
    }
}
