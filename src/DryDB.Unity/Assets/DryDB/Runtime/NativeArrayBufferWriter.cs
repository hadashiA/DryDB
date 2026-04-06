#nullable enable
using System;
using System.Buffers;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace DryDB.Unity
{
    unsafe class NativeArrayBufferWriter<T> : IBufferWriter<T>, IDisposable where T : unmanaged
    {
        public int WrittenCount => offset;
        public int Capacity => buffer.Length;
        public int FreeCapacity => buffer.Length - offset;

        public Span<T> WrittenSpan
        {
            get
            {
                var ptr = buffer.GetUnsafeReadOnlyPtr();
                return new Span<T>(ptr, offset);
            }
        }

        public NativeArray<T> WrittenBuffer
        {
            get
            {
                var ptr = buffer.GetUnsafeReadOnlyPtr();
                var result = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(ptr, offset, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref result, AtomicSafetyHandle.GetTempMemoryHandle());
#endif
                return result;
            }
        }

        const int DefaultInitialBufferSize = 256;

        // Copy of Array.MaxLength.
        // Probably not equal to the NativeArray limit, but used as a reference value.
        const int ArrayMaxLength = 0x7FFFFFC7;

        NativeArray<T> buffer;
        int offset;
        readonly Allocator allocator;
        readonly NativeArrayOptions options;
        readonly NativeArrayMemoryManager<T> memoryManager;

        public NativeArrayBufferWriter(int initialBufferLength, Allocator allocator, NativeArrayOptions options = NativeArrayOptions.UninitializedMemory)
        {
            this.allocator = allocator;
            this.options = options;
            buffer = new NativeArray<T>(initialBufferLength, allocator, options);
            memoryManager = new NativeArrayMemoryManager<T>(buffer);
        }

        public void Advance(int count)
        {
            var newIndex = offset + count;
            if (newIndex > buffer.Length)
            {
                ThrowOutOfRange(newIndex);
            }
            offset = newIndex;
        }

        internal NativeArrayMemoryManager<T> GetMemoryManager() => memoryManager;

        public Memory<T> GetMemory(int sizeHint = 0)
        {
            CheckAndResizeBuffer(sizeHint);
            return memoryManager.Memory[offset..];
        }

        public Span<T> GetSpan(int sizeHint = 0)
        {
            CheckAndResizeBuffer(sizeHint);
            var ptr = (T*)buffer.GetUnsafeReadOnlyPtr();
            ptr += offset;
            return new Span<T>(ptr, buffer.Length - offset);
        }

        public void ResetWrittenCount()
        {
            offset = 0;
        }

        public void Clear()
        {
            buffer.AsSpan().Clear();
            offset = 0;
        }

        public void Dispose()
        {
            buffer.Dispose();
        }

        void CheckAndResizeBuffer(int sizeHint)
        {
            if (sizeHint < 0)
                throw new ArgumentException(nameof(sizeHint));

            if (sizeHint == 0)
            {
                sizeHint = 1;
            }

            if (sizeHint > FreeCapacity)
            {
                var currentLength = buffer.Length;

                // Attempt to grow by the larger of the sizeHint and double the current size.
                var growBy = Math.Max(sizeHint, currentLength);
                if (currentLength == 0)
                {
                    growBy = Math.Max(growBy, DefaultInitialBufferSize);
                }

                var newSize = currentLength + growBy;
                if (newSize > ArrayMaxLength)
                {
                    ThrowOutOfRange(newSize);
                }
                Expand(newSize);
            }
        }

        void Expand(int newLength)
        {
            var newArray = new NativeArray<T>(newLength, allocator, options);
            if (offset > 0)
            {
                var bytesToCopy = offset * sizeof(T);
                UnsafeUtility.MemCpy(buffer.GetUnsafePtr(), newArray.GetUnsafePtr(), bytesToCopy);
            }
            buffer.Dispose();
            buffer = newArray;
            memoryManager.ResetBuffer(buffer);
        }

        void ThrowOutOfRange(int needed)
        {
            throw new NotSupportedException($"Cannot advance past the end of the buffer. {needed}");
        }
    }
}