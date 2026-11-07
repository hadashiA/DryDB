using System.IO;

namespace DryDB.Storages;

/// <summary>
/// The <see cref="StorageFactory"/> that serves <see cref="FileStream"/>s through
/// <see cref="PreadPageLoader"/>. Kept out of
/// <see cref="DatabaseLoadOptions.DefaultStorageFactory"/> so that applications that
/// never read a file do not link file I/O (trimmers and AOT compilers root everything
/// the default factory references); <see cref="ReadOnlyDatabase.OpenFileAsync"/>
/// swaps this factory in. To pass an already opened <see cref="FileStream"/> to
/// <see cref="ReadOnlyDatabase.OpenAsync"/>, set
/// <see cref="DatabaseLoadOptions.StorageFactory"/> to <see cref="Factory"/>.
/// </summary>
public static class FileStorage
{
    public static readonly StorageFactory Factory = (stream, pageSize) =>
        stream is FileStream fs
            ? new PreadPageLoader(fs.SafeFileHandle)
            : DatabaseLoadOptions.DefaultStorageFactory(stream, pageSize);
}
