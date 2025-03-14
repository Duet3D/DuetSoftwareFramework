using DuetAPI.ObjectModel;
using System.Threading;
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
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
        {
            using (await Model.Provider.AccessReadOnlyAsync(cancellationToken))
            {
                if (Enabled == Model.PeriodicUpdater.IsProtocolEnabled(Protocol))
                {
                    // Cannot enable/disable a single protocol multiple times
                    return;
                }
            }

            if (Enabled)
            {
                Model.PeriodicUpdater.ProtocolEnabled(Protocol);
            }
            else
            {
                Model.PeriodicUpdater.ProtocolDisabled(Protocol);
            }

            using (await Model.Provider.AccessReadWriteAsync(cancellationToken))
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
