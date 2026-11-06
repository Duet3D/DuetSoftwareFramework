namespace DuetSharedLibrary;

/// <summary>
/// Firmware modules a firmware update can address, as <c>FirmwareModule</c> in CANlib's RRF3Common.h
/// </summary>
public enum FirmwareModule : byte
{
    /// <summary>The main firmware</summary>
    Main = 0,

    /// <summary>The WiFi module's firmware</summary>
    /// <remarks>Deprecated</remarks>
    // Wifi = 1,

    /// <summary>SBC packages</summary>
    SbcPackages = 2,

    /// <summary>The bootloader</summary>
    Bootloader = 3,

    /// <summary>A PanelDue's firmware</summary>
    PanelDue = 4
}
