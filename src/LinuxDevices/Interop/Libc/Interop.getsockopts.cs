using System.Runtime.InteropServices;

internal struct ucred
{
    public int pid;
    public int uid;
    public int gid;
};

internal partial class Interop
{
    [DllImport(LibcLibrary, SetLastError = true)]
    internal static extern int getsockopt(int sockfd, int level, int optname, ref ucred optval, ref int optlen);

    internal const int SOL_SOCKET = 1;
    internal const int SO_PEERCRED = 17;
}

