using System;
using System.Collections.Generic;
using System.Linq;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the network subsystem
    /// </summary>
    public class Network : ICloneable
    {
        /// <summary>
        /// Name of the machine
        /// </summary>
        public string Name { get; set; }
        
        /// <summary>
        /// Password required to access this machine
        /// </summary>
        /// <remarks>
        /// This concept is deprecated and may be dropped in favour of user authentication in the future
        /// </remarks>
        public string Password { get; set; } = "reprap";

        /// <summary>
        /// List of available network interfaces
        /// </summary>
        /// <seealso cref="NetworkInterface"/>
        public List<NetworkInterface> Interfaces { get; set; } = new List<NetworkInterface>();

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Network
            {
                Name = (Name != null) ? string.Copy(Name) : null,
                Password = (Password != null) ? string.Copy(Password) : null,
                Interfaces = Interfaces.Select(iface => (NetworkInterface)iface.Clone()).ToList()
            };
        }
    }
}
