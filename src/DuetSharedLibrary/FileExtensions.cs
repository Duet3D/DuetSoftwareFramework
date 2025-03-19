using System.IO;
using System.Runtime.InteropServices;

namespace DuetSharedLibrary;

public static class FileExtensions
{
    public static void ChangeOwner(string filename, int uid, int gid)
    {
        int error = Interop.chown(filename, uid, gid);
        if (error < 0)
        {
            throw new IOException($"Failed to change owner of {filename} (error {Marshal.GetLastWin32Error()})");
        }
    }
}