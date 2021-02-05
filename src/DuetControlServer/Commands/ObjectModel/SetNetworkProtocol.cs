using DuetAPI.ObjectModel;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.SetNetworkProtocol"/> command
    /// </summary>
    public sealed class SetNetworkProtocol : DuetAPI.Commands.SetNetworkProtocol
    {
        /// <summary>
        /// Set an atomic property in the object model
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async Task Execute()
        {
            if (Enabled)
            {
                Model.PeriodicUpdater.ProtocolEnabled(Protocol);
            }
            else
            {
                Model.PeriodicUpdater.ProtocolDisabled(Protocol);
            }

            using (await Model.Provider.AccessReadWriteAsync())
            {
                foreach (NetworkInterface iface in Model.Provider.Get.Network.Interfaces)
                {
                    if (Enabled)
                    {
                        iface.ActiveProtocols.Add(Protocol);
                    }
                    else
                    {
                        iface.ActiveProtocols.Remove(Protocol);
                    }
                }
            }
        }
    }
}
