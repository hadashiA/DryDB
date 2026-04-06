#nullable enable
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using UnityEngine;

namespace DryDB.Unity
{
    public class UnityNativeAllocatorPageLoaderException : Exception
    {
        public UnityNativeAllocatorPageLoaderException(string message) : base(message) { }
    }

    public class UnityNativeAllocatorPageLoader : IPageLoader
    {
        static readonly ConcurrentQueue<NativeArrayBufferWriter<byte>> BufferWriterPool = new();

        public static readonly StorageFactory Factory = (stream, pageSize) =>
        {
            if (stream is FileStream fs)
            {
                return new UnityNativeAllocatorPageLoader(fs.Name, pageSize);
            }
            throw new NotSupportedException();
        };

        static NativeArrayBufferWriter<byte> RentBufferWriter(int initialCapacity)
        {
            if (BufferWriterPool.TryDequeue(out var output))
            {
                return output;
            }
            return new NativeArrayBufferWriter<byte>(initialCapacity, Allocator.Persistent);
        }

        static void ReturnBufferWriter(NativeArrayBufferWriter<byte> buffer)
        {
            buffer.ResetWrittenCount();
            BufferWriterPool.Enqueue(buffer);
        }

        readonly string filePath;
        public int PageSize { get; }

        public UnityNativeAllocatorPageLoader(string filePath, int pageSize)
        {
            this.filePath = filePath;
            PageSize = pageSize;
        }

        public void Dispose()
        {
        }

        public async ValueTask<IMemoryOwner<byte>> ReadPageAsync(
            PageNumber pageNumber,
            IPageFilter[]? filters,
            CancellationToken cancellationToken = default)
        {
            var buffer = new NativeArray<byte>(PageSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var commands = new NativeArray<ReadCommand>(1, Allocator.Persistent);

            ReadHandle handle;
            unsafe
            {
                var cmd = new ReadCommand
                {
                    Offset = pageNumber.Value,
                    Size = PageSize,
                    Buffer = (byte*)buffer.GetUnsafePtr()
                };
                commands[0] = cmd;
                handle = AsyncReadManager.Read(
                    filePath,
                    (ReadCommand*)commands.GetUnsafeReadOnlyPtr(),
                    1);
            }

            await Awaitable.BackgroundThreadAsync();
            try
            {
                handle.JobHandle.Complete();
            }
            finally
            {
                handle.Dispose();
            }
            await Awaitable.MainThreadAsync();

            if (filters is { Length: > 0 })
            {
                return ApplyFilter(buffer, filters);
            }
            return new NativeArrayMemoryManager<byte>(buffer);
        }

        public unsafe IMemoryOwner<byte> ReadPage(PageNumber pageNumber, IPageFilter[]? filters = null)
        {
            var buffer = new NativeArray<byte>(PageSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var cmd = new ReadCommand
            {
                Offset = pageNumber.Value,
                Size = PageSize,
                Buffer = (byte*)buffer.GetUnsafePtr()
            };
            var commands = new NativeArray<ReadCommand>(1, Allocator.Persistent);
            commands[0] = cmd;

            try
            {
                var handle = AsyncReadManager.Read(filePath, (ReadCommand*)commands.GetUnsafeReadOnlyPtr(), 1);
                handle.JobHandle.Complete();
                if (filters is { Length: > 0 })
                {
                    return ApplyFilter(buffer, filters);
                }
                return new NativeArrayMemoryManager<byte>(buffer, true);
            }
            catch (Exception)
            {
                buffer.Dispose();
                throw;
            }
            finally
            {
                commands.Dispose();
            }
        }

        NativeArrayMemoryManager<byte> ApplyFilter(NativeArray<byte> source, IPageFilter[] filters)
        {
            var sourceSpan = source.AsSpan();
            var headerSize = this.GetTotalPageHeaderSize();
            var output = RentBufferWriter(source.Length);

            // copy header
            output.Write(sourceSpan[..headerSize]);

            filters[0].Decode(sourceSpan[headerSize..], output);

            // update page header
            var pageHeader = new PageHeader { PageSize = output.WrittenCount };
            MemoryMarshal.Write(output.WrittenSpan, ref pageHeader);

            if (filters.Length <= 1)
            {
                return output.GetMemoryManager();
            }

            // double buffer
            var input = output;
            output = RentBufferWriter(output.WrittenCount);

            for (var i = 1; i < filters.Length; i++)
            {
                // copy header
                output.Write(sourceSpan[..headerSize]);

                // copy node header
                filters[i].Encode(input.WrittenSpan[headerSize..], output);

                // update page header
                pageHeader = new PageHeader { PageSize = output.WrittenCount };
                MemoryMarshal.Write(output.WrittenSpan, ref pageHeader);

                // last
                if (i >= filters.Length - 1)
                {
                    ReturnBufferWriter(input);
                    return output.GetMemoryManager();
                }
                (output, input) = (input, output);
            }
            throw new InvalidOperationException("unreached");
        }

    }
}