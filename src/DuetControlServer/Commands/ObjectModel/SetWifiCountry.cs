using DuetAPI.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.SetWifiCountry"/> command
/// </summary>
/// <param name="model">Object model</param>
public sealed class SetWifiCountry(Model.ObjectModel model) : DuetAPI.Commands.SetWifiCountry
{
    /// <summary>
    /// Set the WiFi country code of every WiFi interface in the object model
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            foreach (NetworkInterface iface in model.Network.Interfaces)
            {
                if (iface.Type == NetworkInterfaceType.WiFi)
                {
                    iface.WifiCountry = CountryCode;
                }
            }
        }
    }
}
