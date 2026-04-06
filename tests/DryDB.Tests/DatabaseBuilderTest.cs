using System;
using System.IO;
using System.Threading.Tasks;
using DryDB.BTree;
using DryDB.Compression;

namespace DryDB.Tests;

[TestFixture]
public class DatabaseBuilderTest
{
    [Test]
    public async Task BuildPrimaryKey()
    {
        var builder = new DatabaseBuilder();

        var itemsTable = builder.CreateTable("items");

        itemsTable.Append("item1"u8.ToArray(), "value1"u8.ToArray());
        itemsTable.Append("item2"u8.ToArray(), "value2"u8.ToArray());
        itemsTable.Append("item3"u8.ToArray(), "value3"u8.ToArray());

        var memoryStream = new MemoryStream(4096 * 2);
        await builder.BuildToStreamAsync(memoryStream);

        memoryStream.Seek(0, SeekOrigin.Begin);
        var catalog = await DryDBCodec.ParseCatalogAsync(memoryStream);
        Assert.That(catalog.PageSize, Is.EqualTo(4096));
        Assert.That(catalog.TableDescriptors.Count, Is.EqualTo(1) );
        Assert.That(catalog.TableDescriptors["items"].Name, Is.EqualTo("items"));

        var primaryKeyDescriptor = catalog.TableDescriptors["items"].PrimaryKeyDescriptor;
        Assert.That(primaryKeyDescriptor.IsUnique, Is.True);
        Assert.That(primaryKeyDescriptor.KeyEncoding, Is.EqualTo(KeyEncoding.Ascii));
        Assert.That(primaryKeyDescriptor.ValueKind, Is.EqualTo(ValueKind.RawData));

        var treeBytes = memoryStream.ToArray().AsSpan((int)primaryKeyDescriptor.RootPageNumber.Value);
        var nodeHeader = NodeHeader.Parse(treeBytes);
        Assert.That(nodeHeader.EntryCount, Is.EqualTo(3));
        Assert.That(nodeHeader.Kind, Is.EqualTo(NodeKind.Leaf));
        Assert.That(nodeHeader.EntryCount, Is.EqualTo(3));
        Assert.That(nodeHeader.LeftSiblingPageNumber.IsEmpty, Is.True);
        Assert.That(nodeHeader.RightSiblingPageNumber.IsEmpty, Is.True);
    }

    [Test]
    public async Task BuildCompressPage()
    {
        var builder = new DatabaseBuilder();
        builder.AddPageFilter(x =>
        {
            x.AddZstandardCompression();
        });

        var itemsTable = builder.CreateTable("items");

        itemsTable.Append("item1"u8.ToArray(), "value1"u8.ToArray());
        itemsTable.Append("item2"u8.ToArray(), "value2"u8.ToArray());
        itemsTable.Append("item3"u8.ToArray(), "value3"u8.ToArray());

        var memoryStream = new MemoryStream(4096 * 2);
        await builder.BuildToStreamAsync(memoryStream);

        memoryStream.Seek(0, SeekOrigin.Begin);
        var catalog = await DryDBCodec.ParseCatalogAsync(memoryStream);
        Assert.That(catalog.Filters!.Count, Is.EqualTo(1));
        Assert.That(catalog.Filters[0], Is.InstanceOf<ZstdCompressionPageFilter>());
    }
}
