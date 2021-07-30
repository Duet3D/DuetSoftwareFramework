using System;
using System.Runtime.InteropServices;

internal partial class Interop
{
    [DllImport(LibcLibrary, SetLastError = true)]
    internal static extern int poll(IntPtr fds, int nfds, int timeout);
}

public unsafe struct pollfd
{
    public int fd { get; set; }
    public short events { get; set; }
    public short revents { get; set; }
}

internal enum PollFlags : short
{
    POLLIN = 0x01,
    POLLPRI = 0x02,
    POLLOUT = 0x04,
    POLLERR = 0x08,
    POLLHUP = 0x10,
    POLLNVAL = 0x20,
    POLLMSG = 0x0400,
    POLLRDHUP = 0x2000
}
