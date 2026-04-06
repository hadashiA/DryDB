using System.Text;
using System.Threading.Tasks;

namespace DryDB.Tests;

[TestFixture]
public class NonUniqueSecondaryIndexQueryTest
{
    [Test]
    public async Task GetAll()
    {
        var table = await TestHelper.BuildTableAsync(
            KeyEncoding.Ascii,
            databaseConfigure: builder => builder.PageSize = 128,
            tableConfigure: builder =>
            {
                var index = 0;
                builder.AddSecondaryIndex("category", false, KeyEncoding.Ascii,
                    (key, value) =>
                    {
                        return $"category:{index++ / 10:D3}";
                    });

                for (var i = 0; i < 1000; i++)
                {
                    builder.Append(
                        Encoding.ASCII.GetBytes($"key{i:D5}"),
                        Encoding.ASCII.GetBytes($"value{i:D5}"));
                }
            });

        var query = table.Index("category");
        var result = query.GetAll("category:020"u8);
        Assert.That(result.Count, Is.EqualTo(10));
        Assert.That(Encoding.ASCII.GetString(result[0].Span), Is.EqualTo("value00200"));
        Assert.That(Encoding.ASCII.GetString(result[9].Span), Is.EqualTo("value00209"));
    }
}
