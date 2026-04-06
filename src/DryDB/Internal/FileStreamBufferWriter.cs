using System;
using System.Buffers;
using System.IO;

namespace DryDB.Internal;

class FileStreamBufferWriter(FileStream stream) : IBufferWriter<byte>
{
    byte[] buffer = new byte[4096];

    public void Advance(int count) => stream.Write(buffer, 0, count);

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (sizeHint > buffer.Length)
        {
            Array.Resize(ref buffer, sizeHint);
        }
        return buffer;
    }

    public Span<byte> GetSpan(int sizeHint = 0) =>
        GetMemory(sizeHint).Span;
}