using System.Collections.Generic;

namespace DryDB.BTree;

/// <summary>
/// Assigns dense page ordinals in flush order while building, and records each
/// page's file offset. The offsets are written to the file as the page directory
/// section (format 1.3); every on-disk page pointer stores the ordinal, and readers
/// translate ordinal -> offset through the directory only on cache misses.
/// </summary>
sealed class PageDirectory
{
    public readonly List<long> Offsets = [];

    public PageNumber Add(long fileOffset)
    {
        Offsets.Add(fileOffset);
        return new PageNumber(Offsets.Count - 1);
    }
}
