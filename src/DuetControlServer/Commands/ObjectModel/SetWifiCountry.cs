using DuetAPI.ObjectModel;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.SetWifiCountry"/> command
    /// </summary>
    public sealed class SetWifiCountry : DuetAPI.Commands.SetWifiCountry
    {
        /// <summary>
        /// Set the WiFi country code of every WiFi interface in the object model
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async Task Execute()
        {
            using (await Model.Provider.AccessReadWriteAsync())
            {
                foreach (NetworkInterface iface in Model.Provider.Get.Network.Interfaces)
                {
                    if (iface.Type == NetworkInterfaceType.WiFi)
                    {
                        iface.WifiCountry = CountryCode;
                    }
                }
            }
        }
    }
}
