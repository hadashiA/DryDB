using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MessagePack;
using DryDB.MessagePack;

namespace DryDB.Tests;

[TestFixture]
public class MessagePackReadOnlyTableTest
{
    [MessagePackObject]
    public class Person
    {
        [Key(0)]
        public string Name { get; set; } = "";

        [Key(1)]
        public int Age { get; set; }
    }

    [Test]
    public async Task Get_ByteKey()
    {
        var table = await BuildMessagePackTableAsync<Person>(
            KeyEncoding.Ascii,
            builder =>
            {
                for (var i = 0; i < 100; i++)
                {
                    builder.Append(
                        Encoding.ASCII.GetBytes($"key{i:D3}"),
                        new Person { Name = $"Person{i}", Age = 20 + i });
                }
            });

        var result = await table.GetAsync("key050"u8.ToArray());
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Name, Is.EqualTo("Person50"));
        Assert.That(result.Age, Is.EqualTo(70));

        var notFound = await table.GetAsync("notfound"u8.ToArray());
        Assert.That(notFound, Is.Null);
    }

    [Test]
    public async Task Get_TypedKey()
    {
        var table = await BuildMessagePackTableAsync<Person>(
            KeyEncoding.Ascii,
            builder =>
            {
                for (var i = 0; i < 100; i++)
                {
                    builder.Append($"key{i:D3}", new Person { Name = $"Person{i}", Age = 20 + i });
                }
            });

        var result = table.Get("key025");
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Name, Is.EqualTo("Person25"));
        Assert.That(result.Age, Is.EqualTo(45));
    }

    [Test]
    public async Task Get_Int64Key()
    {
        var table = await BuildMessagePackTableAsync<Person>(
            KeyEncoding.Int64LittleEndian,
            builder =>
            {
                for (var i = 0; i < 100; i++)
                {
                    builder.Append((long)i, new Person { Name = $"Person{i}", Age = 20 + i });
                }
            });

        var result = await table.GetAsync(42L);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Name, Is.EqualTo("Person42"));
        Assert.That(result.Age, Is.EqualTo(62));
    }

    [Test]
    public async Task GetRange_Between()
    {
        var table = await BuildMessagePackTableAsync<Person>(
            KeyEncoding.Ascii,
            builder =>
            {
                for (var i = 0; i < 100; i++)
                {
                    builder.Append(
                        Encoding.ASCII.GetBytes($"key{i:D3}"),
                        new Person { Name = $"Person{i}", Age = 20 + i });
                }
            });

        var result = await table.GetRangeAsync(
            "key010"u8.ToArray(),
            "key020"u8.ToArray(),
            startKeyExclusive: false,
            endKeyExclusive: false,
            SortOrder.Ascending);

        Assert.That(result.Count, Is.EqualTo(11)); // 010, 011, ..., 020
        Assert.That(result[0].Name, Is.EqualTo("Person10"));
        Assert.That(result[10].Name, Is.EqualTo("Person20"));
    }

    [Test]
    public async Task GetRange_TypedKey()
    {
        var table = await BuildMessagePackTableAsync<Person>(
            KeyEncoding.Int64LittleEndian,
            builder =>
            {
                for (var i = 0; i < 100; i++)
                {
                    builder.Append((long)i, new Person { Name = $"Person{i}", Age = 20 + i });
                }
            });

        var result = table.GetRange(
            10L,
            20L,
            startKeyExclusive: false,
            endKeyExclusive: false,
            SortOrder.Ascending);

        Assert.That(result.Count, Is.EqualTo(11)); // 10, 11, ..., 20
        Assert.That(result[0].Name, Is.EqualTo("Person10"));
        Assert.That(result[10].Name, Is.EqualTo("Person20"));
    }

    static async ValueTask<MessagePackReadOnlyTable<TValue>> BuildMessagePackTableAsync<TValue>(
        IKeyEncoding keyEncoding,
        Action<MessagePackTableBuilder<TValue>> configure)
    {
        var builder = new DatabaseBuilder { PageSize = 128 };
        var tableBuilder = builder.CreateTable("items", keyEncoding)
            .AsMessagePackSerializable<TValue>();
        configure(tableBuilder);

        var memoryStream = new MemoryStream(1024);
        await builder.BuildToStreamAsync(memoryStream);

        memoryStream.Seek(0, SeekOrigin.Begin);
        var database = await ReadOnlyDatabase.OpenAsync(memoryStream);
        var table = database.GetTable("items");
        return table.AsMessagePackSerializable<TValue>();
    }
}