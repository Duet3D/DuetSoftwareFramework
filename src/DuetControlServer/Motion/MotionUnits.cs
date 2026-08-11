using DuetControlServer.Motion.Native;

namespace DuetControlServer.Motion;

/// <summary>
/// Conversions between the units a code gives a value in and the units the planner works in
/// </summary>
/// <remarks>
/// <para>
/// Ported from RepRapFirmware's <c>ConvertSpeedFromMmPerSec</c> and the rest of that family in
/// <c>RepRapFirmware.h</c>. The object model holds each property in the units it is documented in -
/// mm/min for speeds and jerk, mm/s^2 for acceleration, seconds for pressure advance - and the
/// planner works in mm per step clock, so something has to convert between them.
/// </para>
/// <para>
/// The conversions live here rather than being written out where they are needed because they were
/// written out in several places and one of them was wrong: the same value was divided by
/// <c>SecondsPerMinute</c> in one walk of the object model and not in another. A conversion that
/// exists once cannot disagree with itself
/// </para>
/// </remarks>
internal static class MotionUnits
{
    /// <summary>Seconds per minute, for the object model's mm/min speeds</summary>
    private const float SecondsPerMinute = 60.0f;

    /// <summary>Step clocks per second squared</summary>
    private const float StepClockRateSquared = MotionLimits.StepClockRate * MotionLimits.StepClockRate;

    /// <summary>
    /// A speed in mm per second, as mm per step clock
    /// </summary>
    /// <param name="mmPerSec">The speed</param>
    /// <returns>The same speed in planner units</returns>
    public static float SpeedFromMmPerSec(float mmPerSec) => mmPerSec / MotionLimits.StepClockRate;

    /// <summary>
    /// A speed in mm per minute, as mm per step clock
    /// </summary>
    /// <param name="mmPerMin">The speed, as the object model and most codes give it</param>
    /// <returns>The same speed in planner units</returns>
    public static float SpeedFromMmPerMin(float mmPerMin) => mmPerMin / SecondsPerMinute / MotionLimits.StepClockRate;

    /// <summary>
    /// A speed in mm per step clock, back in mm per second
    /// </summary>
    /// <param name="mmPerStepClock">The speed</param>
    /// <returns>The same speed in mm per second</returns>
    public static float SpeedToMmPerSec(float mmPerStepClock) => mmPerStepClock * MotionLimits.StepClockRate;

    /// <summary>
    /// An acceleration in mm per second squared, as mm per step clock squared
    /// </summary>
    /// <param name="mmPerSecSquared">The acceleration, as the object model gives it</param>
    /// <returns>The same acceleration in planner units</returns>
    public static float AccelerationFromMmPerSecSquared(float mmPerSecSquared) => mmPerSecSquared / StepClockRateSquared;

    /// <summary>
    /// A time in seconds, as a number of step clocks
    /// </summary>
    /// <param name="seconds">The time, as pressure advance is given</param>
    /// <returns>The same time in step clocks</returns>
    /// <remarks>A time multiplies by the clock rate where a speed divides by it</remarks>
    public static float ClocksFromSeconds(float seconds) => seconds * MotionLimits.StepClockRate;
}
