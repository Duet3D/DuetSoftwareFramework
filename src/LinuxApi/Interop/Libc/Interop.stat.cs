using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
internal struct statbuf
{
    public uint DeviceID;
    public uint InodeNumber;
    public uint Mode;
    public uint HardLinks;
    public uint UserID;
    public uint GroupID;
    public uint SpecialDeviceID;
    public ulong Size;
    public ulong BlockSize;
    public uint Blocks;
    public long TimeLastAccess;
    public long TimeLastModification;
    public long TimeLastStatusChange;
}

internal static partial class Interop
{
    internal const int STATVER = 1;

    [DllImport(LibcLibrary, EntryPoint = "__xstat", SetLastError = true, CharSet = CharSet.Ansi)]
    internal static extern int stat(int vers, string pathname, ref statbuf statbuf);
}
