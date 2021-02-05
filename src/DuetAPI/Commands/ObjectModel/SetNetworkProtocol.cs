using DuetAPI.ObjectModel;
using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Flag a given network protocol as enabled or disabled
    /// </summary>
    /// <remarks>
    /// The object model must not be locked from the same connection via <see cref="LockObjectModel"/> when this is called!
    /// </remarks>
    [RequiredPermissions(SbcPermissions.SuperUser)]
    public class SetNetworkProtocol : Command
    {
        /// <summary>
        /// Protocol to change
        /// </summary>
        public NetworkProtocol Protocol { get; set; }

        /// <summary>
        /// Whether the protocol is enabled or not
        /// </summary>
        public bool Enabled { get; set; }
    }
}
