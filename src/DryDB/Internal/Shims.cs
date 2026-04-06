#if NETSTANDARD
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace System.Runtime.CompilerServices
{
    static class MemoryMarshalEx
    {
        public static ref T GetArrayDataReference<T>(T[] array)
        {
            return ref MemoryMarshal.GetReference(array.AsSpan());
        }

    }
}

namespace System.IO
{
    static class RandomAccess
    {
        public static int GetLength(SafeFileHandle handle)
        {
            throw new NotImplementedException();
        }

        public static ValueTask<int> ReadAsync(
            SafeFileHandle handle,
            Memory<byte> destination,
            long fileOffset,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }


        public static int Read(
            SafeFileHandle handle,
            Span<byte> buffer,
            long fileOffset)
        {
            throw new NotImplementedException();
        }
    }

    static class StreamExtensions
    {
        public static ValueTask<int> ReadAtLeastAsync(this Stream stream, Memory<byte> buffer, int minimumBytes, bool throwOnEndOfStream = true, CancellationToken cancellationToken = default)
        {
            ValidateReadAtLeastArguments(buffer.Length, minimumBytes);
            return stream.ReadAtLeastAsyncCore(buffer, minimumBytes, throwOnEndOfStream, cancellationToken);
        }

        static void ValidateReadAtLeastArguments(int bufferLength, int minimumBytes)
        {
            if (minimumBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumBytes));
            }
            if (bufferLength < minimumBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferLength));
            }
        }

        // No argument checking is done here. It is up to the caller.
        static async ValueTask<int> ReadAtLeastAsyncCore(this Stream stream, Memory<byte> buffer, int minimumBytes, bool throwOnEndOfStream, CancellationToken cancellationToken)
        {
            var totalRead = 0;
            while (totalRead < minimumBytes)
            {
                var read = await stream.ReadAsync(buffer.Slice(totalRead), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    if (throwOnEndOfStream)
                    {
                        throw new EndOfStreamException();
                    }

                    return totalRead;
                }

                totalRead += read;
            }

            return totalRead;
        }
    }
}
#endif

