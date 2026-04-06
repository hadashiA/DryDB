using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DryDB;

public readonly struct PageSlice(IPageEntry entry, int start, int length) : IDisposable
{
    public IPageEntry Page => entry;
    public int Start => start;
    public int Length => length;

    public ReadOnlySpan<byte> Span
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Page.Memory.Span.Slice(start, length);
    }

    public ReadOnlyMemory<byte> Memory
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Page.Memory.Slice(start, length);
    }

    public void Dispose()
    {
        entry.Release();
    }
}

public interface IPageEntry
{
    public PageNumber PageNumber { get; }
    public ReadOnlyMemory<byte> Memory { get; }
    public void Retain();
    public void Release();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetLength() => Unsafe.ReadUnaligned<int>(ref GetReference());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref byte GetReference() => ref MemoryMarshal.GetReference(Memory.Span);
}
