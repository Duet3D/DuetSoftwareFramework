using System.Runtime.InteropServices;

#pragma warning disable IDE1006 // Naming Styles
internal struct ucred
{
    public int pid;
    public int uid;
    public int gid;
};
#pragma warning restore IDE1006 // Naming Styles

internal partial class Interop
{
    [DllImport(LibcLibrary, SetLastError = true)]
    internal static extern int getsockopt(int sockfd, int level, int optname, ref ucred optval, ref int optlen);

    internal const int SOL_SOCKET = 1;
    internal const int SO_PEERCRED = 17;
}

