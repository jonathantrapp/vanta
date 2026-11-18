using System.Runtime.InteropServices;

namespace HwSuite.Platform;

/// <summary>The few libc calls the Linux backends need (i2c-dev ioctls, flock, evdev reads).</summary>
internal static class LibC
{
    public const int O_RDONLY = 0, O_RDWR = 2, O_CREAT = 0x40, O_NONBLOCK = 0x800, O_CLOEXEC = 0x80000;
    public const int LOCK_EX = 2, LOCK_NB = 4, LOCK_UN = 8;
    public const int EINTR = 4, EAGAIN = 11, EBUSY = 16;

    [DllImport("libc", SetLastError = true)] public static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);
    [DllImport("libc", SetLastError = true)] public static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] public static extern int ioctl(int fd, nuint request, nint arg);
    [DllImport("libc", SetLastError = true)] public static extern int flock(int fd, int operation);
    [DllImport("libc", SetLastError = true)] public static extern nint read(int fd, nint buf, nint count);
    [DllImport("libc")] public static extern uint geteuid();

    public static string Errno() { int e = Marshal.GetLastPInvokeError(); return $"errno {e} ({Marshal.GetPInvokeErrorMessage(e)})"; }
    public static int LastErrno => Marshal.GetLastPInvokeError();

    public static bool IsRoot => OperatingSystem.IsLinux() && geteuid() == 0;
}
