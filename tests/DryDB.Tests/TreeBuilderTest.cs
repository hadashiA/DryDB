using System;
using System.IO;
using System.Threading.Tasks;
using DryDB.BTree;
using DryDB.Internal;

namespace DryDB.Tests;

[TestFixture]
public class TreeBuilderTest
{
    [Test]
    public async Task Build_SingleLeaf()
    {
        var keyValues = new UniqueKeyValueList(KeyEncoding.Ascii)
        {
            { "key1"u8.ToArray(), "value1"u8.ToArray() },
            { "key2"u8.ToArray(), "value2"u8.ToArray() },
            { "key3"u8.ToArray(), "value3"u8.ToArray() }
        };

        var memoryStream = new MemoryStream();
        var buildResult = await TreeBuilder.BuildToAsync(
            memoryStream,
            128,
            keyValues);

        var result = memoryStream.ToArray();
        var header = NodeHeader.Parse(result);

        Assert.That(buildResult.RootPageNumber.Value, Is.EqualTo(0));
        Assert.That(header.Kind, Is.EqualTo(NodeKind.Leaf));
    }

    [Test]
    public async Task Build_Depth2()
    {
        var keyValues = new UniqueKeyValueList(KeyEncoding.Ascii)
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
        };

        var memoryStream = new MemoryStream();
        var buildResult = await TreeBuilder.BuildToAsync(
            memoryStream,
            128,
            keyValues);

        var result = memoryStream.ToArray();

        var header = NodeHeader.Parse(result.AsSpan((int)buildResult.RootPageNumber.Value));
        Assert.That(header.Kind, Is.EqualTo(NodeKind.Internal));
        Assert.That(header.EntryCount, Is.EqualTo(2));

        var internalNode = new InternalNodeReader(result.AsSpan((int)buildResult.RootPageNumber.Value), header.EntryCount);
        internalNode.GetAt(0, out var internalKey1, out var childPosition1);
        internalNode.GetAt(1, out var internalKey2, out var childPosition2);

        Assert.That(internalKey1.SequenceEqual("key01"u8), Is.True);
        Assert.That(internalKey2.SequenceEqual("key06"u8), Is.True);

        header = NodeHeader.Parse(result.AsSpan((int)childPosition1.Value));
        Assert.That(header.Kind, Is.EqualTo(NodeKind.Leaf));
        Assert.That(header.EntryCount, Is.EqualTo(5));
        Assert.That(header.LeftSiblingPageNumber.IsEmpty, Is.True);
        // Assert.That(header.RightSiblingPosition, Is.EqualTo());

    //     var leafNode1 = new LeafNodeReader(header, payload);
    //     leafNode1.GetAtOffset(0, out var key1, out var value1);
    //     Assert.That(key1.SequenceEqual("key01"u8));
    //     Assert.That(value1.SequenceEqual("value01"u8));
    //
    //     NodeHeader.Parse(result.AsSpan((int)childPosition2), out header, out payload);
    //     Assert.That(header.Kind, Is.EqualTo(NodeKind.Leaf));
    //     Assert.That(header.EntryCount, Is.EqualTo(4));
    //     Assert.That(header.LeftSiblingPageNumber, Is.EqualTo(new PageNumber(0)));
    //
    //     var leafNode2 = new LeafNodeReader(header, payload);
    //     leafNode2.GetAtOffset(0, out key1, out value1);
    //     Assert.That(key1.SequenceEqual("key07"u8));
    //     Assert.That(value1.SequenceEqual("value07"u8));
    }
}
