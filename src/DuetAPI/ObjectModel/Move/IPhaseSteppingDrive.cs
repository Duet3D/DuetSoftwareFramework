namespace DuetAPI.ObjectModel;

/// <summary>
/// What M970 configures, which an axis and an extruder answer alike
/// </summary>
/// <remarks>
/// RepRapFirmware addresses both through one logical drive number, so the code that reads M970 does
/// not distinguish them either. The two object model classes have no common base, so this is what
/// lets one piece of code write to both rather than two that must be kept in step
/// </remarks>
public interface IPhaseSteppingDrive
{
    /// <summary>Whether the drive is currently using phase stepping</summary>
    bool? PhaseStep { get; set; }

    /// <summary>Velocity feedforward gain of the phase stepping control loop</summary>
    float PhaseStepKv { get; set; }

    /// <summary>Acceleration feedforward gain of the phase stepping control loop</summary>
    float PhaseStepKa { get; set; }
}
