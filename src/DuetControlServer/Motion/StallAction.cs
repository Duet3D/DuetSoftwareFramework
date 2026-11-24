namespace DuetControlServer.Motion;

/// <summary>
/// What is done when a driver stalls, as chosen by M915's R parameter
/// </summary>
/// <remarks>An expansion board raises an event for every action but <see cref="None"/></remarks>
public enum StallAction : byte
{
    /// <summary>R0: nothing</summary>
    None = 0,

    /// <summary>R1: log the stall, which only a driver on a RepRapFirmware main board does without raising an event</summary>
    Log = 1,

    /// <summary>R2: raise an event</summary>
    RaiseEvent = 2,

    /// <summary>R3: the same as <see cref="RaiseEvent"/></summary>
    RaiseEventAlias = 3
}
