using DuetAPI.ObjectModel;
using DuetControlServer.Model;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.SetNetworkProtocol"/> command
/// </summary>
public sealed class SetNetworkProtocol(Model.ObjectModel model, Model.PeriodicUpdater periodicUpdater) : DuetAPI.Commands.SetNetworkProtocol
{
    /// <summary>
    /// Set an atomic property in the object model
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (Enabled == periodicUpdater.IsProtocolEnabled(Protocol))
            {
                // Cannot enable/disable a single protocol multiple times
                return;
            }
        }

        if (Enabled)
        {
            periodicUpdater.ProtocolEnabled(Protocol);
        }
        else
        {
            periodicUpdater.ProtocolDisabled(Protocol);
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            foreach (NetworkInterface iface in model.Network.Interfaces)
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
