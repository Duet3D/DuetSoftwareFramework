using System.Runtime.InteropServices;

internal partial class Interop
{
    [DllImport(LibcLibrary, SetLastError = true, CharSet = CharSet.Ansi)]
    internal static extern int chmod(string pathname, ushort mode);
}
