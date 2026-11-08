using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DryDB.Storages;

namespace DryDB.Tests;

[TestFixture]
public class OsFileCacheControlTest
{
    // Proves the variadic-fcntl trampoline transmits the argument correctly on this
    // platform's ABI: set FD_CLOEXEC (= 1) through our Fcntl and read it back. With
    // a broken ABI the argument would be garbage and the round-trip would not
    // return exactly 1.
    [Test]
    public void Fcntl_RoundTripsArgument()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.Ignore("fcntl trampoline is only used on macOS");
        }

        var path = Path.GetTempFileName();
        try
        {
            using var fs = File.OpenRead(path);
            Assert.That(OsFileCacheControl.Fcntl(fs.SafeFileHandle, OsFileCacheControl.F_SETFD, 1), Is.EqualTo(0));
            Assert.That(OsFileCacheControl.Fcntl(fs.SafeFileHandle, OsFileCacheControl.F_GETFD, 0), Is.EqualTo(1));
            Assert.That(OsFileCacheControl.Fcntl(fs.SafeFileHandle, OsFileCacheControl.F_SETFD, 0), Is.EqualTo(0));
            Assert.That(OsFileCacheControl.Fcntl(fs.SafeFileHandle, OsFileCacheControl.F_GETFD, 0), Is.EqualTo(0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void TryDisableOsCaching_SucceedsOnMacOs()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.Ignore("F_NOCACHE is only implemented on macOS");
        }

        var path = Path.GetTempFileName();
        try
        {
            using var fs = File.OpenRead(path);
            Assert.That(OsFileCacheControl.TryDisableOsCaching(fs.SafeFileHandle), Is.True);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Reads_Work_With_BypassOsPageCache()
    {
        var path = Path.Combine(Path.GetTempPath(), $"drydb_nocache_{Guid.NewGuid():N}.drydb");
        try
        {
            using (var builder = new DatabaseBuilder { PageSize = 4096 })
            {
                var table = builder.CreateTable("items", KeyEncoding.Int64LittleEndian);
                for (var i = 0; i < 1000; i++)
                {
                    table.Append(i, Encoding.UTF8.GetBytes($"value{i:D5}"));
                }
                await builder.BuildToFileAsync(path);
            }

            using var database = await ReadOnlyDatabase.OpenFileAsync(path, new DatabaseLoadOptions
            {
                BypassOsPageCache = true,
            });
            var items = database.GetTable("items");
            for (var i = 0; i < 1000; i += 97)
            {
                using var result = items.Get((long)i);
                Assert.That(result.HasValue, Is.True);
                Assert.That(Encoding.UTF8.GetString(result.Value.Span), Is.EqualTo($"value{i:D5}"));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
