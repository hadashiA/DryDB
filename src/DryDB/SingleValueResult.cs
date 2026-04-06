using System;

namespace DryDB;

public readonly struct SingleValueResult : IDisposable
{
    public static SingleValueResult Empty => new(default, false);

    public readonly PageSlice Value;
    public readonly bool HasValue;

    internal SingleValueResult(PageSlice value, bool hasValue)
    {
        Value = value;
        HasValue = hasValue;
    }

    public void Dispose()
    {
        if (HasValue)
        {
            Value.Dispose();
        }
    }
}