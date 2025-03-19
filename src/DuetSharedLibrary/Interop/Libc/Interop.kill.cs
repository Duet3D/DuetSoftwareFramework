using System.Runtime.InteropServices;

internal static partial class Interop
{
    internal const int SIGTERM = 15;

    [DllImport(LibcLibrary, SetLastError = true)]
    internal static extern int kill(int pid, int signal);
}
