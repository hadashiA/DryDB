using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DryDB.Internal;

public static class DuplicateKey
{
    public static int SizeOf(int sourceLength) => sourceLength + sizeof(int);

    public static bool TryEncode(
        ReadOnlySpan<byte> sourceKey,
        int valueId,
        Span<byte> destination)
    {
        ref var ptr = ref MemoryMarshal.GetReference(destination);
        Unsafe.CopyBlockUnaligned(
            ref ptr,
            ref MemoryMarshal.GetReference(sourceKey),
            (uint)sourceKey.Length);
        ptr = ref Unsafe.Add(ref ptr, sourceKey.Length);
        Unsafe.WriteUnaligned(ref ptr, valueId);
        return true;
    }

    public static byte[] Generate(ReadOnlySpan<byte> sourceKey, int valueId)
    {
        var buffer = new byte[sourceKey.Length + sizeof(int)];
        TryEncode(sourceKey, valueId, buffer);
        return buffer;
    }
}
