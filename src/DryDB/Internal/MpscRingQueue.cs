namespace DryDB.Internal;

using System;
using System.Threading;

/// <summary>
/// Multi procuder single consumer fixed-size lock-free queue
/// </summary>
/// <typeparam name="T"></typeparam>
sealed class MpscRingQueue<T> where T : class
{
    struct Slot
    {
        public T? Item;
        public long Sequence;
    }

    readonly Slot[] buffer;
    readonly int mask;   // 2pod
    long head;           // Update by dequeue (single consumer)
    long tail;           // Update by enqueue (multiple producer)

    public int Capacity => buffer.Length;

    public MpscRingQueue(int capacityPowerOfTwo)
    {
        if (capacityPowerOfTwo <= 0 || (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
        {
            throw new ArgumentException("capacity must be power of two", nameof(capacityPowerOfTwo));
        }

        mask = capacityPowerOfTwo - 1;
        head = 0;
        tail = 0;

        buffer = new Slot[capacityPowerOfTwo];
        for (var i = 0; i < capacityPowerOfTwo; i++)
        {
            buffer[i].Sequence = i;
        }
    }

    public bool TryEnqueue(T item)
    {
        while (true)
        {
            var t = Volatile.Read(ref tail);
            var index = t & mask;
            ref var slot = ref buffer[index];
            var seq = Volatile.Read(ref slot.Sequence);
            var diff = seq - t;

            switch (diff)
            {
                case < 0:
                    // full
                    return false;
                case 0 when Interlocked.CompareExchange(ref tail, t + 1, t) == t:
                    slot.Item = item;
                    Volatile.Write(ref slot.Sequence, t + 1);
                    return true;
            }
            // retry
        }
    }

    public bool TryDequeue(out T item)
    {
        while (true)
        {
            var h = Volatile.Read(ref head);
            var index = h & mask;
            ref var slot = ref buffer[index];
            var seq = Volatile.Read(ref slot.Sequence);
            var diff = seq - (h + 1);

            switch (diff)
            {
                case 0:
                    item = slot.Item!;
                    slot.Item = null;
                    Volatile.Write(ref slot.Sequence, h + buffer.Length);
                    Volatile.Write(ref head, h + 1);
                    return true;
                case < 0:
                    item = null!;
                    return false; // empty
            }
            // retry
        }
    }
}
