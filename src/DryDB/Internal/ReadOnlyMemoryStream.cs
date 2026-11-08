using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DryDB.Internal;

/// <summary>
/// Seekable read-only <see cref="Stream"/> view over a <see cref="ReadOnlyMemory{T}"/>,
/// used to feed the stream-based catalog parser without copying the caller's buffer.
/// </summary>
sealed class ReadOnlyMemoryStream(ReadOnlyMemory<byte> memory) : Stream
{
    int position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => memory.Length;

    public override long Position
    {
        get => position;
        set
        {
            if (value < 0 || value > memory.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            position = (int)value;
        }
    }

    public override int Read(Span<byte> buffer)
    {
        var length = Math.Min(buffer.Length, memory.Length - position);
        memory.Span.Slice(position, length).CopyTo(buffer);
        position += length;
        return length;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int ReadByte() =>
        position < memory.Length ? memory.Span[position++] : -1;

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        cancellationToken.IsCancellationRequested
            ? ValueTask.FromCanceled<int>(cancellationToken)
            : new ValueTask<int>(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => memory.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
