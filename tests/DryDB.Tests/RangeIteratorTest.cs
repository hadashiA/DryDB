using System.Text;
using System.Threading.Tasks;
using DryDB.Internal;

namespace DryDB.Tests;

[TestFixture]
public class RangeIteratorTest
{
    [Test]
    public async Task MoveNext_FirstValue()
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

        var iterator = tree.CreateIterator();
        Assert.That(iterator.MoveNext(), Is.True);

        Assert.That(
            Encoding.ASCII.GetString(iterator.Current.Span),
            Is.EqualTo("value1"));
    }

    [Test]
    public async Task Seek()
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

        var iterator = tree.CreateIterator();
        Assert.That(iterator.TrySeek("key10"u8), Is.False);
        Assert.That(iterator.TrySeek("key5"u8), Is.True);

        Assert.That(
            Encoding.ASCII.GetString(iterator.Current.Span),
            Is.EqualTo("value5"));

        Assert.That(iterator.MoveNext(), Is.True);
        Assert.That(
            Encoding.ASCII.GetString(iterator.Current.Span),
            Is.EqualTo("value7"));

        Assert.That(iterator.MoveNext(), Is.True);
        Assert.That(
            Encoding.ASCII.GetString(iterator.Current.Span),
            Is.EqualTo("value8"));

        Assert.That(iterator.MoveNext(), Is.True);
        Assert.That(
            Encoding.ASCII.GetString(iterator.Current.Span),
            Is.EqualTo("value9"));

        Assert.That(iterator.MoveNext(), Is.False);
    }

    [Test]
    public async Task MoveNextAsync_FirstValue()
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

        var iterator = tree.CreateIterator();
        var result = await iterator.MoveNextAsync();
        Assert.That(result, Is.True);

        Assert.That(
            Encoding.ASCII.GetString(iterator.Current.Span),
            Is.EqualTo("value1"));
    }
}