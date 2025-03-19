using System.Runtime.InteropServices;

internal struct Ucred
{
    public int pid;
    public int uid;
    public int gid;
};

internal partial class Interop
{
    [DllImport(LibcLibrary, SetLastError = true)]
    internal static extern int getsockopt(int sockfd, int level, int optname, ref Ucred optval, ref int optlen);

    internal const int SOL_SOCKET = 1;
    internal const int SO_PEERCRED = 17;
}

