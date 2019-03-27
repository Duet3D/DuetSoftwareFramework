using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.LinuxRequests
{
    /// <summary>
    /// Request the object model of the given module
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct GetObjectModel
    {
        /// <summary>
        /// Module index to query
        /// </summary>
        /// <seealso cref="Consts.NumModules"/>
        public byte Module;
    }
}