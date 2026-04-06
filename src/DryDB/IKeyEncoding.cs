using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace DryDB;

public interface IKeyEncoding : IComparer<ReadOnlyMemory<byte>>
{
    /// <summary>
    /// A string that uniquely identifies the encoding method. It is embedded in the binary.
    /// </summary>
    string Id { get; }

    int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b);

    int GetMaxEncodedByteCount<TKey>(TKey key)
        where TKey : IComparable<TKey>;

    /// <summary>
    /// Encodes any type to a byte array held by the DB
    /// </summary>
    /// <exception cref="KeyEncodingMismatchException">
    /// If TKey is an unsupported type
    /// </exception>
    /// <returns>
    ///  false if the destination length is insufficient.
    /// </returns>
    bool TryEncode<TKey>(TKey key, Span<byte> destination, out int bytesWritten)
        where TKey : IComparable<TKey>;

    bool TryEncode(string formattedString, Span<byte> destination, out int bytesWritten);

    /// <summary>
    /// Formats a key byte array to a human-readable UTF-8 string.
    /// </summary>
    /// <returns>
    /// false if the destination length is insufficient.
    /// </returns>
    bool TryFormat(ReadOnlySpan<byte> key, Span<byte> destination, out int bytesWritten);
}

public static class KeyEncoding
{
    public static Int64LittleEndianEncoding Int64LittleEndian => Int64LittleEndianEncoding.Instance;
    public static AsciiOrdinalEncoding Ascii => AsciiOrdinalEncoding.Instance;
#if NET9_0_OR_GREATER
    public static Uuidv7KeyEncoding Uuidv7 => Uuidv7KeyEncoding.Instance;
#endif

    static readonly ConcurrentDictionary<string, IKeyEncoding> registry = new();

    public static IKeyEncoding FromId(string id)
    {
        switch (id)
        {
            case "i64":
                return Int64LittleEndianEncoding.Instance;
            case "ascii":
                return AsciiOrdinalEncoding.Instance;
#if NET9_0_OR_GREATER
            case "uuidv7":
                return Uuidv7KeyEncoding.Instance;
#endif
            default:
                if (registry.TryGetValue(id, out var encoding))
                {
                    return encoding;
                }
                throw new InvalidOperationException($"Unknown encoding id: `{id}`");
        }
    }

    public static void Register(IKeyEncoding customEncoding)
    {
        if (!registry.TryAdd(customEncoding.Id, customEncoding))
        {
            throw new ArgumentException($"The encoding {customEncoding.Id} already exists.");
        }
    }
}

public sealed class Int64LittleEndianEncoding : IKeyEncoding
{
    public static readonly Int64LittleEndianEncoding Instance = new();

    public string Id => "i64";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var na = Unsafe.ReadUnaligned<long>(ref MemoryMarshal.GetReference(a));
        var nb = Unsafe.ReadUnaligned<long>(ref MemoryMarshal.GetReference(b));
        return (na > nb ? 1 : 0) - (na < nb ? 1 : 0);
    }

    public int GetMaxEncodedByteCount<TKey>(TKey key)
        where TKey : IComparable<TKey> => sizeof(long);

    public bool TryEncode<TKey>(TKey key, Span<byte> destination, out int bytesWritten)
        where TKey : IComparable<TKey>
    {
        long longKey = 0;
        switch (key)
        {
            case long i64:
                longKey = i64;
                break;
            case int i32:
                longKey = i32;
                break;
            case short i16:
                longKey = i16;
                break;
            case ulong u64:
                longKey = (long)u64;
                break;
            case uint u32:
                longKey = u32;
                break;
            case ushort u16:
                longKey = u16;
                break;
            default:
                KeyEncodingMismatchException.Throw(typeof(TKey), "int64");
                break;
        }

        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(destination), longKey);
        bytesWritten = sizeof(long);
        return true;
    }

    public bool TryEncode(string formattedString, Span<byte> destination, out int bytesWritten)
    {
        if (long.TryParse(formattedString, out var longValue))
        {
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(destination), longValue);
            bytesWritten = sizeof(long);
        }
        throw new KeyEncodingMismatchException($"Cannot convert to int64: {formattedString}");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Compare(ReadOnlyMemory<byte> a, ReadOnlyMemory<byte> b)
    {
        var na = Unsafe.ReadUnaligned<long>(ref MemoryMarshal.GetReference(a.Span));
        var nb = Unsafe.ReadUnaligned<long>(ref MemoryMarshal.GetReference(b.Span));
        return (na > nb ? 1 : 0) - (na < nb ? 1 : 0);
    }

    public bool TryFormat(ReadOnlySpan<byte> key, Span<byte> destination, out int bytesWritten)
    {
        if (key.Length < sizeof(long))
        {
            bytesWritten = 0;
            return false;
        }
        var value = Unsafe.ReadUnaligned<long>(ref MemoryMarshal.GetReference(key));
#if NET8_0_OR_GREATER
        return value.TryFormat(destination, out bytesWritten);
#else
        var str = value.ToString();
        if (destination.Length < str.Length)
        {
            bytesWritten = 0;
            return false;
        }
        bytesWritten = Encoding.UTF8.GetBytes(str, destination);
        return true;
#endif
    }
}

public sealed class AsciiOrdinalEncoding : IKeyEncoding
{
    public static readonly AsciiOrdinalEncoding Instance = new();

    public string Id => "ascii";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Compare(ReadOnlyMemory<byte> x, ReadOnlyMemory<byte> y)
    {
        return Compare(x.Span, y.Span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        return a.SequenceCompareTo(b);
    }

    public int GetMaxEncodedByteCount<TKey>(TKey key) where TKey : IComparable<TKey>
    {
        var stringKey = Unsafe.As<TKey, string>(ref key);
        return Encoding.ASCII.GetMaxByteCount(stringKey.Length);
    }

    public bool TryEncode<TKey>(TKey key, Span<byte> destination, out int bytesWritten)
        where TKey : IComparable<TKey>
    {
        if (key is string keyString)
        {
#if NET8_0_OR_GREATER
            return Encoding.ASCII.TryGetBytes(keyString, destination, out bytesWritten);
#else
            try
            {
                bytesWritten = Encoding.ASCII.GetBytes(keyString, destination);
                return true;
            }
            catch (ArgumentException ex)
            {
                bytesWritten = 0;
                return false;
            }
#endif
        }
        KeyEncodingMismatchException.Throw(typeof(TKey), "string");
        bytesWritten = default;
        return default;
    }

    public bool TryEncode(string formattedString, Span<byte> destination, out int bytesWritten)
    {
#if NET8_0_OR_GREATER
        return Encoding.ASCII.TryGetBytes(formattedString, destination, out bytesWritten);
#else
        try
        {
            bytesWritten = Encoding.ASCII.GetBytes(formattedString, destination);
            return true;
        }
        catch (ArgumentException ex)
        {
            bytesWritten = 0;
            return false;
        }
#endif
    }

    public bool TryFormat(ReadOnlySpan<byte> key, Span<byte> destination, out int bytesWritten)
    {
        if (destination.Length < key.Length)
        {
            bytesWritten = 0;
            return false;
        }
        key.CopyTo(destination);
        bytesWritten = key.Length;
        return true;
    }
}

#if NET9_0_OR_GREATER
public sealed class Uuidv7KeyEncoding : IKeyEncoding
{
    public static readonly Uuidv7KeyEncoding Instance = new();

    public string Id => "uuidv7";

    public int Compare(ReadOnlyMemory<byte> x, ReadOnlyMemory<byte> y)
    {
        return Compare(x.Span, y.Span);
    }

    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var aId = new Guid(a);
        var bId = new Guid(b);
        return aId.CompareTo(bId);
    }

    public int GetMaxEncodedByteCount<TKey>(TKey key) where TKey : IComparable<TKey> => 16;

    public bool TryEncode<TKey>(TKey key, Span<byte> destination, out int bytesWritten) where TKey : IComparable<TKey>
    {
        if (key is Guid { Version: 7 } keyGuid)
        {
            if (destination.Length >= 16)
            {
                bytesWritten = 16;
                return keyGuid.TryWriteBytes(destination);
            }
            bytesWritten = default;
            return false;
        }
        KeyEncodingMismatchException.Throw(typeof(TKey), "uuidv7");
        bytesWritten = default;
        return false;
    }

    public bool TryEncode(string formattedString, Span<byte> destination, out int bytesWritten)
    {
        if (Guid.TryParse(formattedString, out var guid))
        {
            bytesWritten = 16;
            return guid.TryWriteBytes(destination);
        }
        throw new KeyEncodingMismatchException($"Cannot convert to uuidv7: {formattedString}");
    }

    public bool TryFormat(ReadOnlySpan<byte> key, Span<byte> destination, out int bytesWritten)
    {
        if (key.Length < 16)
        {
            bytesWritten = 0;
            return false;
        }
        var guid = new Guid(key);
        return guid.TryFormat(destination, out bytesWritten, "D");
    }
}
#endif
