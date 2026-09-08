using System;
using System.Text;
using DryDB.UlidKey;

namespace DryDB.Tests;

/// <summary>
/// Digests are mandatory (format 1.4), so every encoding must satisfy the
/// order-preserving contract: digest(a) &lt; digest(b) implies Compare(a, b) &lt; 0.
/// Exercised pairwise over randomized keys per encoding.
/// </summary>
[TestFixture]
public class KeyDigestContractTest
{
    static void AssertOrderPreserving(IKeyEncoding encoding, byte[][] keys)
    {
        for (var i = 0; i < keys.Length; i++)
        {
            for (var j = 0; j < keys.Length; j++)
            {
                var da = encoding.GetKeyDigest(keys[i]);
                var db = encoding.GetKeyDigest(keys[j]);
                var compared = encoding.Compare(keys[i], keys[j]);
                if (da < db)
                {
                    Assert.That(compared, Is.LessThan(0), $"digest order broken at [{i}] vs [{j}]");
                }
                else if (da > db)
                {
                    Assert.That(compared, Is.GreaterThan(0), $"digest order broken at [{i}] vs [{j}]");
                }
                // Equal digests are ambiguous by contract; nothing to assert.
            }
        }
    }

    [Test]
    public void Int64_Digest_IsExactAndOrderPreserving()
    {
        var random = new Random(1234);
        var keys = new byte[200][];
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = BitConverter.GetBytes(
                i < 8 ? new[] { long.MinValue, -1L, 0L, 1L, long.MaxValue, -256L, 255L, 42L }[i]
                      : random.NextInt64(long.MinValue, long.MaxValue));
        }
        AssertOrderPreserving(KeyEncoding.Int64LittleEndian, keys);

        // Exactness: the digest round-trips back to the original key bytes.
        Assert.That(((IKeyEncoding)KeyEncoding.Int64LittleEndian).IsKeyDigestExact, Is.True);
        foreach (var key in keys)
        {
            var digest = KeyEncoding.Int64LittleEndian.GetKeyDigest(key);
            var decoded = new byte[8];
            Assert.That(
                ((IKeyEncoding)KeyEncoding.Int64LittleEndian).TryDecodeKeyFromDigest(digest, decoded, out var written),
                Is.True);
            Assert.That(written, Is.EqualTo(8));
            Assert.That(decoded.AsSpan().SequenceEqual(key), Is.True);
        }
    }

    [Test]
    public void Ascii_Digest_IsOrderPreserving()
    {
        var random = new Random(1234);
        var keys = new byte[200][];
        for (var i = 0; i < keys.Length; i++)
        {
            // Mixed lengths, including shared prefixes and keys shorter than 8 bytes.
            var length = random.Next(0, 20);
            var s = new StringBuilder();
            for (var c = 0; c < length; c++)
            {
                s.Append((char)('a' + random.Next(4)));
            }
            keys[i] = Encoding.ASCII.GetBytes(s.ToString());
        }
        AssertOrderPreserving(KeyEncoding.Ascii, keys);
    }

#if NET9_0_OR_GREATER
    [Test]
    public void Uuidv7_Digest_IsOrderPreserving()
    {
        // Guid.CompareTo orders by unsigned field comparison, not by encoded byte
        // order; the digest packs the first three fields in comparison order.
        var random = new Random(1234);
        var keys = new byte[300][];
        for (var i = 0; i < keys.Length; i++)
        {
            // Random guids stress the field packing beyond timestamp-ordered v7 ones.
            var guid = i % 3 == 0 ? Guid.CreateVersion7() : Guid.NewGuid();
            keys[i] = guid.ToByteArray();
        }
        AssertOrderPreserving(KeyEncoding.Uuidv7, keys);
    }
#endif

    [Test]
    public void Ulid_Digest_IsOrderPreserving()
    {
        var random = new Random(1234);
        var keys = new byte[300][];
        for (var i = 0; i < keys.Length; i++)
        {
            // Random 16-byte patterns rather than NewUlid: exercises arbitrary byte
            // values including equal prefixes.
            keys[i] = new byte[16];
            random.NextBytes(keys[i]);
            if (i % 10 == 0 && i > 0)
            {
                Array.Copy(keys[i - 1], keys[i], 8); // force digest collisions
            }
        }
        AssertOrderPreserving(UlidKeyEncoding.Instance, keys);
    }
}
