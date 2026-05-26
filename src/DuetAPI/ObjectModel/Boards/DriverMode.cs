namespace DuetAPI.ObjectModel;

/// <summary>
/// Operating mode of a stepper driver
/// </summary>
public enum DriverMode
{
    /// <summary>
    /// Constant off-time chopper
    /// </summary>
    ConstantOffTime = 0,

    /// <summary>
    /// Random off-time chopper
    /// </summary>
    RandomOffTime = 1,

    /// <summary>
    /// SpreadCycle
    /// </summary>
    SpreadCycle = 2,

    /// <summary>
    /// StealthChop (includes stealthChop2)
    /// </summary>
    StealthChop = 3,

    /// <summary>
    /// Field-oriented control (direct)
    /// </summary>
    Direct = 4,

    /// <summary>
    /// Driver mode is unknown
    /// </summary>
    Unknown = 5
}
