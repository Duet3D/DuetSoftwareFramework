using System.Runtime.InteropServices;

internal partial class Interop
{
    [DllImport(LibcLibrary, SetLastError = true)]
    internal static extern int chown(string pathname, int owner, int group);

    [DllImport(LibcLibrary, SetLastError = true)]
    internal static extern int fchown(int fd, int owner, int group);

    [DllImport(LibcLibrary, SetLastError = true)]
    internal static extern int lchown(string pathname, int owner, int group);
}
