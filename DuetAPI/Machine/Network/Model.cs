using System;
using System.Collections.Generic;
using System.Linq;

namespace DuetAPI.Machine.Network
{
    public class Model : ICloneable
    {
        public string Name { get; set; }
        public string Password { get; set; } = "reprap";

        public List<NetworkInterface> Interfaces { get; set; } = new List<NetworkInterface>();

        public object Clone()
        {
            return new Model
            {
                Name = (Name != null) ? string.Copy(Name) : null,
                Password = (Password != null) ? string.Copy(Password) : null,
                Interfaces = Interfaces.Select(iface => (NetworkInterface)iface.Clone()).ToList()
            };
        }
    }
}
