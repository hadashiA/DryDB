using System;
using System.Buffers;
using System.Collections.Concurrent;

namespace DryDB.Internal;

static class BufferWriterPool
{
    static readonly ConcurrentQueue<PoolableArrayBufferWriter<byte>> pool = new();

    public static PoolableArrayBufferWriter<byte> Rent(int capacity)
    {
        if (!pool.TryDequeue(out var buffer))
        {
            buffer = new PoolableArrayBufferWriter<byte>(capacity);
        }
        return buffer;
    }

    public static void Return(PoolableArrayBufferWriter<byte> buffer)
    {
        buffer.ResetWrittenCount();
        pool.Enqueue(buffer);
    }
}

sealed class PooledBuffer<T>(T[] array, int offset, int length) : IMemoryOwner<T>
{
    public Memory<T> Memory => new(array, offset, length);

    public void Dispose()
    {
        ArrayPool<T>.Shared.Return(array);
    }
}

class PoolableArrayBufferWriter<T> : IBufferWriter<T>
{
    const int ArrayMaxLength = 0x7FFFFFC7;
    const int DefaultInitialBufferSize = 256;

    T[] _buffer;
    int _index;

    public PoolableArrayBufferWriter(int initialCapacity)
    {
        if (initialCapacity <= 0)
            throw new ArgumentException(null, nameof(initialCapacity));

        _buffer = ArrayPool<T>.Shared.Rent(initialCapacity);
        _index = 0;
    }

    public ReadOnlyMemory<T> WrittenMemory => _buffer.AsMemory(0, _index);
    public ReadOnlySpan<T> WrittenSpan => _buffer.AsSpan(0, _index);
    public int WrittenCount => _index;
    public int Capacity => _buffer.Length;
    public int FreeCapacity => _buffer.Length - _index;

    public void Clear()
    {
        _buffer.AsSpan(0, _index).Clear();
        _index = 0;
    }

    public void ResetWrittenCount() => _index = 0;

    public void Write(ReadOnlySpan<T> value)
    {
        CheckAndResizeBuffer(value.Length);
        value.CopyTo(_buffer.AsSpan(_index));
        Advance(value.Length);
    }

    public void Advance(int count)
    {
        if (count < 0)
            throw new ArgumentException(null, nameof(count));

        if (_index > _buffer.Length - count)
        {
            ThrowInvalidOperationException_AdvancedTooFar(_buffer.Length);
        }

        _index += count;
    }

    public Memory<T> GetMemory(int sizeHint = 0)
    {
        CheckAndResizeBuffer(sizeHint);
        return _buffer.AsMemory(_index);
    }

    public Span<T> GetSpan(int sizeHint = 0)
    {
        CheckAndResizeBuffer(sizeHint);
        return _buffer.AsSpan(_index);
    }

    public IMemoryOwner<T> ToPoolableMemory() => new PooledBuffer<T>(_buffer, 0, _index);

    void CheckAndResizeBuffer(int sizeHint)
    {
        if (sizeHint < 0)
            throw new ArgumentException(null, nameof(sizeHint));

        if (sizeHint == 0)
        {
            sizeHint = 1;
        }

        if (sizeHint > FreeCapacity)
        {
            var currentLength = _buffer.Length;

            // Attempt to grow by the larger of the sizeHint and double the current size.
            var growBy = Math.Max(sizeHint, currentLength);

            if (currentLength == 0)
            {
                growBy = Math.Max(growBy, DefaultInitialBufferSize);
            }

            var newSize = currentLength + growBy;

            if ((uint)newSize > int.MaxValue)
            {
                // Attempt to grow to ArrayMaxLength.
                var needed = (uint)(currentLength - FreeCapacity + sizeHint);

                if (needed > ArrayMaxLength)
                {
                    ThrowOutOfMemoryException(needed);
                }

                newSize = ArrayMaxLength;
            }

            var newBuffer = ArrayPool<T>.Shared.Rent(newSize);
            Buffer.BlockCopy(
                _buffer,
                0,
                newBuffer,
                0,
                _buffer.Length);

            ArrayPool<T>.Shared.Return(_buffer);
            _buffer = newBuffer;
        }
    }

    static void ThrowInvalidOperationException_AdvancedTooFar(int capacity)
    {
        throw new InvalidOperationException($"Cannot advance past the end of the buffer, which has a size of {capacity}.");
    }

    static void ThrowOutOfMemoryException(uint capacity)
    {
        throw new OutOfMemoryException($"Cannot allocate a buffer of size {capacity}.");
    }
}