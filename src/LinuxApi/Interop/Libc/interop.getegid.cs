using System.Runtime.InteropServices;

internal partial class Interop
{
    [DllImport(LibcLibrary)]
    internal static extern int getegid();
}

