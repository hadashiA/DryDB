using System.Collections.Generic;

namespace DryDB.Internal;

class DuplicateComparer<T>(IComparer<T> sourceComparer) : IComparer<T>
{
    public int Compare(T? x, T? y)
    {
        var result = sourceComparer.Compare(x, y);
        return result == 0 ? 1 : result; // Handle equality as being greater
    }
}