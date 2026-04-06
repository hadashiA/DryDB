using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace DryDB.Internal;

/// <summary>
/// Key encoding with int 32 `rid` appended at the end. Mainly for BTree that allows duplicate keys.
/// </summary>
class DuplicateKeyEncoding(IKeyEncoding sourceEncoding) : IKeyEncoding
{
    public string Id => sourceEncoding.Id;

    public int Compare(ReadOnlyMemory<byte> a, ReadOnlyMemory<byte> b) =>
        Compare(a.Span, b.Span);

    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var aOriginal = a[..^sizeof(int)];
        var bOriginal = b[..^sizeof(int)];
        var sourceResult = sourceEncoding.Compare(aOriginal, bOriginal);
        if (sourceResult == 0)
        {
            ref var aPtr = ref MemoryMarshal.GetReference(a);
            ref var bPtr = ref MemoryMarshal.GetReference(b);

            var aValueId = Unsafe.ReadUnaligned<int>(
                ref Unsafe.Add(ref aPtr, aOriginal.Length));
            var bValueId = Unsafe.ReadUnaligned<int>(
                ref Unsafe.Add(ref bPtr, bOriginal.Length));

            if (aValueId < bValueId) return -1;
            if (aValueId > bValueId) return 1;
            return 0;
        }
        return sourceResult;
    }

    public int GetMaxEncodedByteCount<TKey>(TKey key) where TKey : IComparable<TKey>
    {
        return sourceEncoding.GetMaxEncodedByteCount(key) + sizeof(int);
    }

    public bool TryEncode<TKey>(TKey key, Span<byte> destination, out int bytesWritten)
        where TKey : IComparable<TKey>
    {
        throw new NotImplementedException();
    }

    public bool TryEncode(string formattedString, Span<byte> destination, out int bytesWritten)
    {
        throw new NotImplementedException();
    }

    public bool TryFormat(ReadOnlySpan<byte> key, Span<byte> destination, out int bytesWritten)
    {
        var originalKey = key[..^sizeof(int)];
        if (!sourceEncoding.TryFormat(originalKey, destination, out var keyBytesWritten))
        {
            bytesWritten = 0;
            return false;
        }

        ref var keyPtr = ref MemoryMarshal.GetReference(key);
        var valueId = Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref keyPtr, originalKey.Length));

        var remaining = destination[keyBytesWritten..];
        if (remaining.Length < 1)
        {
            bytesWritten = 0;
            return false;
        }
        remaining[0] = (byte)'-';

#if NET8_0_OR_GREATER
        if (!valueId.TryFormat(remaining[1..], out var valueIdBytesWritten))
        {
            bytesWritten = 0;
            return false;
        }
#else
        var valueIdStr = valueId.ToString();
        if (remaining.Length - 1 < valueIdStr.Length)
        {
            bytesWritten = 0;
            return false;
        }
        var valueIdBytesWritten = Encoding.UTF8.GetBytes(valueIdStr, remaining[1..]);
#endif

        bytesWritten = keyBytesWritten + 1 + valueIdBytesWritten;
        return true;
    }
}