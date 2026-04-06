namespace DryDB.Tests;

[TestFixture]
public class KeyComparerTest
{
    [Test]
    public void Hoge()
    {
        var comparer = AsciiOrdinalEncoding.Instance;

        Assert.That(
            comparer.Compare(
                "key01"u8.ToArray(),
                "key02"u8.ToArray()),
            Is.LessThan(0));
    }
}