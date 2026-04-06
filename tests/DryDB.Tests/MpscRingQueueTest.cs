using DryDB.Internal;

namespace DryDB.Tests;

[TestFixture]
public class MpscRingQueueTest
{
    class Entry
    {
        public int Value {  get; set; }
    }

    [Test]
    public void EnqueueDequeue()
    {
        var queue = new MpscRingQueue<Entry>(4);

        Assert.That(queue.TryEnqueue(new Entry { Value = 1111 }), Is.True);

        Assert.That(queue.TryDequeue(out var entry), Is.True);
        Assert.That(entry!.Value, Is.EqualTo(1111));
        Assert.That(queue.TryDequeue(out _), Is.False);

        Assert.That(queue.TryEnqueue(new Entry { Value = 222 }), Is.True);
        Assert.That(queue.TryEnqueue(new Entry { Value = 333 }), Is.True);
        Assert.That(queue.TryEnqueue(new Entry { Value = 444 }), Is.True);
        Assert.That(queue.TryEnqueue(new Entry { Value = 555 }), Is.True);
        Assert.That(queue.TryEnqueue(new Entry { Value = 555 }), Is.False);

        Assert.That(queue.TryDequeue(out entry), Is.True);
        Assert.That(entry!.Value, Is.EqualTo(222));
        Assert.That(queue.TryDequeue(out entry), Is.True);
        Assert.That(entry!.Value, Is.EqualTo(333));
        Assert.That(queue.TryDequeue(out entry), Is.True);
        Assert.That(entry!.Value, Is.EqualTo(444));
        Assert.That(queue.TryDequeue(out entry), Is.True);
        Assert.That(entry!.Value, Is.EqualTo(555));
        Assert.That(queue.TryDequeue(out _), Is.False);

    }
}