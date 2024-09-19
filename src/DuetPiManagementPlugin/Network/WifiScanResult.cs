namespace DuetPiManagementPlugin.Network
{
    /// <summary>
    /// WiFi scan result item
    /// </summary>
    public sealed class WifiScanResultItem
    {
        /// <summary>
        /// SSID
        /// </summary>
        public required string SSID { get; set; }

        /// <summary>
        /// Channel
        /// </summary>
        public required int Chan { get; set; }

        /// <summary>
        /// RSSI
        /// </summary>
        public required int RSSI { get; set; }

        /// <summary>
        /// Phy Mode
        /// </summary>
        public required string Phymode { get; set; }

        /// <summary>
        /// Auth mode
        /// </summary>
        public required string Auth { get; set; }

        /// <summary>
        /// MAC address
        /// </summary>
        public required string MAC { get; set; }
    }

    /// <summary>
    /// WiFi scan result
    /// </summary>
    public sealed class WifiScanResult
    {
        /// <summary>
        /// Array of discovered networks
        /// </summary>
        public required WifiScanResultItem[] NetworkScanResults { get; set; }

        /// <summary>
        /// Error code
        /// </summary>
        public int Err { get; set; }
    }
}