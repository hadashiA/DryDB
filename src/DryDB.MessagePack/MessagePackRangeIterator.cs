namespace DryDB.MessagePack;

public class MessagePackRangeIterator<TValue> //:
    // IEnumerable<TValue>,
    // IEnumerator<TValue>,
    // IAsyncEnumerable<TValue>
{
    readonly RangeIterator inner;

    internal MessagePackRangeIterator(RangeIterator inner)
    {
        this.inner = inner;
    }
}