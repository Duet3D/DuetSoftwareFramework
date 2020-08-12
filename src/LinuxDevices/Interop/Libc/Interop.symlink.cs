using System.Runtime.InteropServices;

internal partial class Interop
{
    [DllImport(LibcLibrary, CharSet = CharSet.Ansi, SetLastError = true)]
    public static extern int symlink(string name1, string name2);
}

