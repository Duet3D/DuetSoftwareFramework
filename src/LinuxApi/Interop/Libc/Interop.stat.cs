using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Explicit, Size = 144)]
internal struct statbuf
{
    [FieldOffset(28)]
    public int st_uid;

    [FieldOffset(32)]
    public int st_gid;
}

internal static partial class Interop
{
    internal const int STATVER = 1;

    [DllImport("libc", EntryPoint = "__xstat", SetLastError = true)]
    internal static extern int stat(int vers, string pathname, ref statbuf statbuf);
}
