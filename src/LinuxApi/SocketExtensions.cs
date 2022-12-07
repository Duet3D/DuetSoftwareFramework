using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace LinuxApi
{
    /// <summary>
    /// Extension methods for UNIX sockets
    /// </summary>
    public static class SocketExtensions
    {
        /// <summary>
        /// Get the peer credentials from a given socket
        /// </summary>
        /// <param name="socket">Socket</param>
        /// <param name="pid">Process ID</param>
        /// <param name="uid">User ID</param>
        /// <param name="gid">Group ID</param>
        public static void GetPeerCredentials(this Socket socket, out int pid, out int uid, out int gid)
        {
            Ucred cred = new();
            int credSize = Marshal.SizeOf<Ucred>();

            int error = Interop.getsockopt(socket.Handle.ToInt32(), Interop.SOL_SOCKET, Interop.SO_PEERCRED, ref cred, ref credSize);
            if (error < 0)
            {
                throw new IOException($"Failed to query peer credentials (error {Marshal.GetLastWin32Error()})");
            }

            pid = cred.pid;
            uid = cred.uid;
            gid = cred.gid;
        }
    }
}
