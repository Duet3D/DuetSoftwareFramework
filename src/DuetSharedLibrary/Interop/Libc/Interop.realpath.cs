using System;
using System.Runtime.InteropServices;

internal partial class Interop
{
    [DllImport(LibcLibrary, SetLastError = true)]
    internal static extern IntPtr realpath([MarshalAs(UnmanagedType.LPStr)] string path, IntPtr resolved);
}
