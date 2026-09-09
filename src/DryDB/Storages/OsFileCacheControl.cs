using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DryDB.Storages;

/// <summary>
/// Best-effort control over the OS page cache for an open file. Currently
/// implemented for macOS/iOS via <c>fcntl(F_NOCACHE)</c>; a no-op elsewhere.
/// </summary>
static class OsFileCacheControl
{
    // <sys/fcntl.h>
    internal const int F_GETFD = 1;
    internal const int F_SETFD = 2;
    internal const int F_NOCACHE = 48;

    /// <summary>
    /// Asks the OS not to retain this file's pages in its own page cache, so the
    /// only in-memory copy of the data is the caller's cache. Returns false when
    /// the platform has no such control or the call failed (callers treat it as a
    /// hint, never an error).
    /// </summary>
    public static bool TryDisableOsCaching(SafeFileHandle handle)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return false;
        }
        return Fcntl(handle, F_NOCACHE, 1) == 0;
    }

    /// <summary>
    /// fcntl(fd, cmd, arg) for platforms where fcntl is variadic.
    /// </summary>
    /// <remarks>
    /// fcntl's third parameter is variadic, and the Apple arm64 ABI passes
    /// variadic arguments on the stack while a naive 3-parameter P/Invoke would
    /// pass it in a register (the callee would then read garbage). The import
    /// below declares eight fixed integer parameters after <c>cmd</c> so that the
    /// ninth lands in the caller's outgoing stack area — exactly where the callee's
    /// va_list starts on arm64. On x86-64 (System V) the first variadic slot is
    /// instead the third integer register, i.e. <paramref name="arg"/>'s first
    /// duplicate. Passing the value in every slot satisfies both ABIs.
    /// </remarks>
    internal static int Fcntl(SafeFileHandle handle, int cmd, long arg)
    {
        var addedRef = false;
        try
        {
            handle.DangerousAddRef(ref addedRef);
            var fd = (int)handle.DangerousGetHandle();
            return fcntl(fd, cmd, arg, arg, arg, arg, arg, arg, arg);
        }
        finally
        {
            if (addedRef)
            {
                handle.DangerousRelease();
            }
        }
    }

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    static extern int fcntl(
        int fd, int cmd,
        long pad2, long pad3, long pad4, long pad5, long pad6, long pad7,
        long stackArg);
}
