using System;
using System.Buffers;
using System.Collections.Generic;

namespace DryDB.Internal;

sealed class MemoryArena : IDisposable
{
    readonly int slabSize;
    readonly List<IMemoryOwner<byte>> slabs = [];
    Memory<byte> current;
    int offset;
    bool disposed;

    public MemoryArena(int initialSlabSize = 64 * 1024)
    {
        slabSize = Math.Max(4096, initialSlabSize);
        AddSlab(slabSize);
    }

    public Memory<byte> Alloc(int size, int alignment = 1)
    {
        if (disposed) throw new ObjectDisposedException(nameof(MemoryArena));
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));

        if (alignment < 1 || (alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentException("alignment must be power of two", nameof(alignment));
        }

        var aligned = AlignUp(offset, alignment);
        if (aligned + size > current.Length)
        {
            var nextSize = Math.Max(slabSize, AlignUp(size, 4096));
            AddSlab(nextSize);
            aligned = 0;
        }

        var mem = current.Slice(aligned, size);
        offset = aligned + size;
        return mem;
    }

    public void Reset()
    {
        if (disposed) throw new ObjectDisposedException(nameof(MemoryArena));
        for (var i = 1; i < slabs.Count; i++) slabs[i].Dispose();
        if (slabs.Count > 1) slabs.RemoveRange(1, slabs.Count - 1);

        current = slabs[0].Memory;
        offset = 0;
    }

    void AddSlab(int size)
    {
        var owner = MemoryPool<byte>.Shared.Rent(size);
        slabs.Add(owner);
        current = owner.Memory;
        offset = 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        foreach (var t in slabs)
        {
            t.Dispose();
        }
        slabs.Clear();
        current = Memory<byte>.Empty;
        offset = 0;
        disposed = true;
    }

    static int AlignUp(int value, int alignment) =>
        (value + (alignment - 1)) & ~(alignment - 1);
}
