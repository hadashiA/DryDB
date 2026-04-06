#nullable enable
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using System;
using System.Buffers;

namespace DryDB.Unity
{
    static unsafe class NativeArrayExtensions
    {
        public static unsafe Memory<T> AsMemory<T>(this NativeArray<T> nativeArray) where T : unmanaged
        {
            return new NativeArrayMemoryManager<T>((T*)nativeArray.GetUnsafeReadOnlyPtr(), nativeArray.Length).Memory;
        }

        public static unsafe ReadOnlyMemory<T> AsMemory<T>(this NativeArray<T>.ReadOnly nativeArray) where T : unmanaged
        {
            return new NativeArrayMemoryManager<T>((T*)nativeArray.GetUnsafeReadOnlyPtr(), nativeArray.Length).Memory;
        }
    }

    unsafe class NativeArrayMemoryManager<T> : MemoryManager<T> where T : unmanaged
    {
        public T* Ptr { get; private set; }
        public int Length { get; private set; }

        NativeArray<T> originalArray;
        readonly bool arrayOwned;

        public NativeArrayMemoryManager(NativeArray<T> nativeArray, bool arrayOwned = false)
            : this((T*)nativeArray.GetUnsafeReadOnlyPtr(), nativeArray.Length)
        {
            originalArray = nativeArray;
            this.arrayOwned = arrayOwned;
        }

        public NativeArrayMemoryManager(T* ptr, int length)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            Ptr = ptr;
            Length = length;
        }

        public void ResetBuffer(NativeArray<T> buffer)
        {
            originalArray = buffer;
            Ptr = (T*)buffer.GetUnsafeReadOnlyPtr();
            Length = buffer.Length;
        }

        public override Span<T> GetSpan() => new(Ptr, Length);

        /// <summary>
        /// Provides access to a pointer that represents the data (note: no actual pin occurs)
        /// </summary>
        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if (elementIndex < 0 || elementIndex >= Length)
            {
                throw new ArgumentOutOfRangeException(nameof(Length));
            }
            return new MemoryHandle(Ptr + elementIndex);
        }

        /// <summary>
        /// Has no effect
        /// </summary>
        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
            if (arrayOwned)
            {
                originalArray.Dispose();
            }
        }
    }
}
