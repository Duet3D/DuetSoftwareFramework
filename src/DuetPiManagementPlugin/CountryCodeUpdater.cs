using DuetAPI.ObjectModel;
using DuetAPIClient;
using DuetPiManagementPlugin.Network;
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DuetPiManagementPlugin
{
    /// <summary>
    /// Class used to determine and update the WiFi country code automatically
    /// </summary>
    public static class CountryCodeUpdater
    {
        /// <summary>
        /// Connection to use for object model updates
        /// </summary>
        private static readonly CommandConnection _connection = new();

        /// <summary>
        /// Initialize country code updater
        /// </summary>
        /// <param name="socketPath">Socket path to connect to</param>
        /// <returns></returns>
        public static async Task Init(string socketPath)
        {
            // Connect to DCS and update the country code initially
            await _connection.Connect(socketPath);
            await UpdateCountryCode();

            // Watch for changes of wpa_supplicant.conf and then update it again
            FileSystemWatcher watcher = new()
            {
                Path = "/etc/default",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
            };

            // Add event handlers.
            watcher.Changed += new FileSystemEventHandler(OnChanged);
            watcher.Created += new FileSystemEventHandler(OnChanged);
            watcher.Renamed += new RenamedEventHandler(OnRenamed);
            watcher.EnableRaisingEvents = true;
        }

        private static async void OnChanged(object source, FileSystemEventArgs e)
        {
            if (Path.GetFileName(e.FullPath) == "crda")
            {
                await UpdateCountryCode();
            }
        }

        private static async void OnRenamed(object source, RenamedEventArgs e)
        {
            if (Path.GetFileName(e.FullPath) == "crda")
            {
                await UpdateCountryCode();
            }
        }

        private static bool _updatingCountryCode = false;

        private static async Task UpdateCountryCode()
        {
            if (_updatingCountryCode)
            {
                return;
            }
            _updatingCountryCode = true;

            await Task.Delay(10000);

            try
            {
                string? countryCode = null;
                if (File.Exists("/etc/default/crda"))
                {
                    // CRDA may override cfg80211, so we check this first
                    try
                    {
                        Regex regex = new(@"REGDOMAIN\s*=\s*(\w\w)");
                        var lines = await File.ReadAllLinesAsync("/etc/default/crda");
                        foreach (string line in lines)
                        {
                            Match match = regex.Match(line);
                            if (match.Success)
                            {
                                countryCode = match.Groups[1].Value;
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.ToString());
                    }
                }
                else
                {
                    // Try to fall back to wpa_supplicant
                    countryCode = await WPA.GetCountryCode();
                    if (string.IsNullOrWhiteSpace(countryCode) && File.Exists("/sys/module/cfg80211/parameters/ieee80211_regdom"))
                    {
                        // If CRDA is not present and wpa_supplicant didn't set the regdom, it may be configured via cfg80211
                        countryCode = await File.ReadAllTextAsync("/sys/module/cfg80211/parameters/ieee80211_regdom");
                    }
                }

                ObjectModel model = await _connection.GetObjectModel();
                await using (await _connection.LockObjectModel())
                {
                    for (int i = 0; i < model.Network.Interfaces.Count; i++)
                    {
                        NetworkInterface iface = model.Network.Interfaces[i];
                        if (iface.Type == NetworkInterfaceType.WiFi)
                        {
                            await _connection.SetObjectModel($"network/interfaces/{i}/wifiCountry", string.IsNullOrWhiteSpace(countryCode) ? "null" : $"\"{countryCode.Trim()}\"");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[err] Failed to update country code: " + e.Message);
            }

            _updatingCountryCode = false;
        }
    }
}
