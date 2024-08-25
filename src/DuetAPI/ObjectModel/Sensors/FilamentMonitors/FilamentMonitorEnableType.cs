namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Enumeration of supported filament sensors
    /// </summary>
    public enum FilamentMonitorEnableMode : int
    {
        /// <summary>
        /// Filament monitor is disabled
        /// </summary>
        Disabled = 0,

        /// <summary>
        /// Filament monitor is enabled during prints from SD card
        /// </summary>
        Enabled = 1,

        /// <summary>
        /// Filament monitor is always enabled (when printing from USB)
        /// </summary>
        AlwaysEnabled = 2
    }
}
