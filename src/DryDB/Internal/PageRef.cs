using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DryDB;

readonly record struct PageRef(PageNumber PageNumber, int Start, int Length)
{
    public static bool TryParse(ReadOnlySpan<byte> source, out PageRef pageRef)
    {
        if (source.Length < Unsafe.SizeOf<PageRef>())
        {
            pageRef = default;
            return false;
        }

        ref var ptr = ref MemoryMarshal.GetReference(source);
        pageRef = Unsafe.ReadUnaligned<PageRef>(ref ptr);
        return true;
    }

    public static PageRef Parse(ReadOnlySpan<byte> source)
    {
        return Unsafe.ReadUnaligned<PageRef>(ref MemoryMarshal.GetReference(source));
    }

    public bool TryEncode(Span<byte> destination)
    {
        if (destination.Length < Unsafe.SizeOf<PageRef>()) return false;

        ref var ptr = ref MemoryMarshal.GetReference(destination);
        Unsafe.WriteUnaligned(ref ptr, this);
        return true;
    }

    public byte[] Encode()
    {
        var result = new byte[Unsafe.SizeOf<PageRef>()];
        TryEncode(result);
        return result;
    }
}