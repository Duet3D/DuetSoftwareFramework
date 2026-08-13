using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Link;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Kinematics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// The machine configuration M-codes, ported from RepRapFirmware's <c>GCodes::HandleMcode</c>
/// </summary>
/// <remarks>
/// <para>
/// These are dispatched from the one switch in <see cref="ProcessAsync"/> like every other M-code;
/// only their bodies live here, to keep that switch readable as it grows.
/// </para>
/// <para>
/// Most of these write the object model and nothing else: <c>move.axes[]</c>,
/// <c>move.extruders[]</c> and <c>move.motionSystems[]</c> are the configuration, and
/// <see cref="Motion.MotionParameters"/> is rebuilt from them by
/// <see cref="Motion.MovePlanner.ReconfigureAsync"/>. There is deliberately no second copy of a
/// setting anywhere in this file.
/// </para>
/// <para>
/// The geometry is the exception, and is being made the rule: M665, M666 and M669 configure
/// <see cref="Motion.MovePlanner.Geometry"/> and write <c>move.kinematics</c> from it, rather than
/// writing the object model and having the geometry derived back out of it. §14 of
/// <c>docs/devel/MCODE_MIGRATION.md</c> is why, and step 4 there applies the same shape to the axes
/// and extruders.
/// </para>
/// <para>
/// RepRapFirmware supports drivers on the main board and drivers on CAN-connected expansion boards,
/// and most of these codes carry two implementations because of it. Here there is only the second
/// kind, so the local-hardware half of each code is not ported and every driver is addressed over CAN.
/// </para>
/// <para>
/// Codes that change what a microstep means - steps per mm, microstepping, driver mapping - wait for
/// the machine to stop first. See <see cref="Motion.MovePlanner.WaitForStandstillAsync"/> for why
/// flushing the code pipeline is not sufficient on its own.
/// </para>
/// </remarks>
internal partial class MCodeHandler
{
    /// <summary>Steps per mm may not be zero or negative (RepRapFirmware's MinimumStepsPerMm)</summary>
    private const float MinStepsPerMm = 0.01f;

    /// <summary>Minimum acceleration in mm/s^2 (RepRapFirmware's MinimumAcceleration)</summary>
    private const float MinAcceleration = 0.1f;

    /// <summary>Minimum jerk in mm/s (RepRapFirmware's MinimumJerk)</summary>
    private const float MinJerkMmPerSec = 0.1f;

    /// <summary>Absolute floor for the minimum movement speed, in mm/s (RepRapFirmware's AbsoluteMinFeedrate)</summary>
    private const float AbsoluteMinFeedrateMmPerSec = 0.001f;

    /// <summary>Seconds per minute, for the object model's mm/min speeds</summary>
    private const float SecondsPerMinute = 60.0f;

    /// <summary>Lowest input shaping frequency RepRapFirmware accepts, in Hz</summary>
    private const float MinShapingFrequency = 1.0f;

    /// <summary>Highest input shaping frequency RepRapFirmware accepts, in Hz</summary>
    private const float MaxShapingFrequency = 1000.0f;

    /// <summary>Smallest speed or extrusion factor M220 and M221 accept, as a fraction</summary>
    private const float MinOverrideFactor = 0.01f;

    /// <summary>How far one relative M290 may babystep, in mm</summary>
    private const float MaxRelativeBabystep = 1.0f;

    /// <summary>
    /// Shortest interval in ms between an endstop reporting twice
    /// </summary>
    /// <remarks>
    /// Zero, because an endstop change is what stops a move: delaying a report to debounce it would
    /// delay the stop by the same amount
    /// </remarks>
    private const ushort EndstopMinReportInterval = 0;

    /// <summary>
    /// The reply when the motion engine would not take the new machine description
    /// </summary>
    /// <remarks>
    /// The engine refuses a description it cannot plan for, and a code that changed the configuration
    /// and reported success anyway leaves the machine running on the description it had before with
    /// nothing said. §13.4 of <c>docs/devel/MCODE_MIGRATION.md</c> makes that visible at the next
    /// move; returning this makes it visible at the code that caused it, in the same words
    /// </remarks>
    private static Message MotionConfigRejected
        => new(MessageType.Error, "The motion configuration was not applied; no moves can be planned until it is");

    /// <summary>
    /// M92: set or report the steps per mm of each drive
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleStepsPerMmAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        // S is the microstepping the given values are quoted at, which lets a configuration be
        // written against one microstepping and used at another
        code.TryGetUInt('S', out uint quotedAtMicrostepping);

        List<RemoteDrivers.DriverValue<(float, int, bool)>> toUpdate = [];
        bool seen = false;
        string? report = null;

        if (SetsAnyDrive(code) && !await FlushAndWaitForStandstillAsync(code, cancellationToken))
        {
            throw new OperationCanceledException();
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Move move = model.Move;

            foreach (Axis axis in move.Axes)
            {
                if (code.TryGetFloat(axis.Letter, out float value))
                {
                    axis.StepsPerMm = ScaleForMicrostepping(value, quotedAtMicrostepping, axis.Microstepping.Value);
                    AddDrivers(toUpdate, axis.Drivers, axis.StepsPerMm, axis.Microstepping);
                    seen = true;
                }
            }

            if (TryGetExtruderValues(code, move, out float[]? extruderValues))
            {
                for (int i = 0; i < extruderValues.Length; i++)
                {
                    Extruder extruder = move.Extruders[i];
                    extruder.StepsPerMm = ScaleForMicrostepping(extruderValues[i], quotedAtMicrostepping, extruder.Microstepping.Value);
                    AddDriver(toUpdate, extruder.Driver, extruder.StepsPerMm, extruder.Microstepping);
                }
                seen = true;
            }

            if (!seen)
            {
                report = ReportPerDrive(move, "Steps/mm: ",
                                        axis => axis.StepsPerMm.ToString("F3", CultureInfo.InvariantCulture),
                                        extruder => extruder.StepsPerMm.ToString("F3", CultureInfo.InvariantCulture));
            }
        }

        if (!seen)
        {
            return new Message(MessageType.Success, report!);
        }

        if (!await planner.ReconfigureAsync(cancellationToken))
        {
            return MotionConfigRejected;
        }
        return await UpdateRemoteDriversAsync(toUpdate, cancellationToken);
    }

    /// <summary>
    /// M201: set or report the acceleration of each drive. M201.1 does the same for the reduced
    /// accelerations used by probing and stall homing moves
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleAccelerationsAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.MinorNumber > 1)
        {
            return new Message(MessageType.Error, $"M201.{code.MinorNumber} is not supported");
        }
        bool reduced = code.MinorNumber == 1;

        bool seen = false;
        string? report = null;

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Move move = model.Move;

            foreach (Axis axis in move.Axes)
            {
                if (code.TryGetFloat(axis.Letter, out float value))
                {
                    float acceleration = MathF.Max(value, MinAcceleration);
                    if (reduced)
                    {
                        axis.ReducedAcceleration = acceleration;
                    }
                    else
                    {
                        axis.Acceleration = acceleration;
                    }
                    seen = true;
                }
            }

            if (TryGetExtruderValues(code, move, out float[]? extruderValues))
            {
                // An extruder has no reduced acceleration of its own in the object model; probing and
                // stall homing moves do not extrude, so M201.1 has nothing to set for one
                if (!reduced)
                {
                    for (int i = 0; i < extruderValues.Length; i++)
                    {
                        move.Extruders[i].Acceleration = MathF.Max(extruderValues[i], MinAcceleration);
                    }
                }
                seen = true;
            }

            if (!seen)
            {
                report = ReportPerDrive(move, reduced ? "Reduced accelerations (mm/sec^2): " : "Accelerations (mm/sec^2): ",
                                        axis => (reduced ? axis.ReducedAcceleration : axis.Acceleration).ToString("F1", CultureInfo.InvariantCulture),
                                        extruder => extruder.Acceleration.ToString("F1", CultureInfo.InvariantCulture));
            }
        }

        if (!seen)
        {
            return new Message(MessageType.Success, report!);
        }

        return await planner.ReconfigureAsync(cancellationToken) ? new Message() : MotionConfigRejected;
    }

    /// <summary>
    /// M203: set or report the maximum speed of each drive and the slowest a move may run
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleMaxFeedratesAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        // Values are in mm/min unless S1 says they are in mm/sec
        bool mmPerSec = code.GetInt('S', 0) == 1;
        float toMmPerMin = mmPerSec ? SecondsPerMinute : 1.0f;

        bool seen = false;
        string? report = null;

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Move move = model.Move;

            // The minimum first, because the maximum rates are held at or above it
            if (code.TryGetFloat('I', out float minimumSpeed))
            {
                move.MinimumMovementSpeed = MathF.Max(minimumSpeed * toMmPerMin / SecondsPerMinute, AbsoluteMinFeedrateMmPerSec);
                seen = true;
            }

            float minimumMmPerMin = move.MinimumMovementSpeed * SecondsPerMinute;
            foreach (Axis axis in move.Axes)
            {
                if (code.TryGetFloat(axis.Letter, out float value))
                {
                    axis.Speed = MathF.Max(value * toMmPerMin, minimumMmPerMin);
                    seen = true;
                }
            }

            if (TryGetExtruderValues(code, move, out float[]? extruderValues))
            {
                for (int i = 0; i < extruderValues.Length; i++)
                {
                    move.Extruders[i].Speed = MathF.Max(extruderValues[i] * toMmPerMin, minimumMmPerMin);
                }
                seen = true;
            }

            if (!seen)
            {
                float fromMmPerMin = mmPerSec ? 1.0f / SecondsPerMinute : 1.0f;
                report = ReportPerDrive(move, $"Max speeds ({(mmPerSec ? "mm/sec" : "mm/min")}): ",
                                        axis => (axis.Speed * fromMmPerMin).ToString("F1", CultureInfo.InvariantCulture),
                                        extruder => (extruder.Speed * fromMmPerMin).ToString("F1", CultureInfo.InvariantCulture))
                         + ", min. speed "
                         + (move.MinimumMovementSpeed * SecondsPerMinute * fromMmPerMin).ToString("F2", CultureInfo.InvariantCulture);
            }
        }

        if (!seen)
        {
            return new Message(MessageType.Success, report!);
        }

        return await planner.ReconfigureAsync(cancellationToken) ? new Message() : MotionConfigRejected;
    }

    /// <summary>
    /// M204: set or report the acceleration limits that apply to a move as a whole
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleMoveAccelerationsAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        bool seen = false;
        string? report = null;

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            MotionSystem motionSystem = GetOrCreateMotionSystem(model.Move);

            // S sets both, for Marlin compatibility. P and T are the ones to use, and either may
            // override what S just set
            if (code.TryGetFloat('S', out float both))
            {
                motionSystem.PrintingAcceleration = motionSystem.TravelAcceleration = MathF.Max(both, MinAcceleration);
                seen = true;
            }
            if (code.TryGetFloat('P', out float printing))
            {
                motionSystem.PrintingAcceleration = MathF.Max(printing, MinAcceleration);
                seen = true;
            }
            if (code.TryGetFloat('T', out float travel))
            {
                motionSystem.TravelAcceleration = MathF.Max(travel, MinAcceleration);
                seen = true;
            }

            if (!seen)
            {
                report = string.Format(CultureInfo.InvariantCulture,
                                       "Maximum printing acceleration {0:F1}, maximum travel acceleration {1:F1} mm/sec^2",
                                       motionSystem.PrintingAcceleration, motionSystem.TravelAcceleration);
            }
        }

        if (!seen)
        {
            return new Message(MessageType.Success, report!);
        }

        return await planner.ReconfigureAsync(cancellationToken) ? new Message() : MotionConfigRejected;
    }

    /// <summary>
    /// M205 and M566: set or report the instantaneous speed change allowed at a junction
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// The two codes differ in units and in which limit they write. M205 is in mm/sec and sets only
    /// the jerk used while printing; M566 is in mm/min and sets the machine limit, which also pulls
    /// the printing jerk down to it
    /// </remarks>
    private async ValueTask<Message> HandleJerkAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        bool mmPerSec = code.MajorNumber == 205;
        bool setMax = code.MajorNumber == 566;
        float toMmPerMin = mmPerSec ? SecondsPerMinute : 1.0f;
        float minJerkMmPerMin = MinJerkMmPerSec * SecondsPerMinute;

        bool seenAxis = false, seenExtruder = false;
        string? report = null;

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Move move = model.Move;

            foreach (Axis axis in move.Axes)
            {
                if (code.TryGetFloat(axis.Letter, out float value))
                {
                    SetJerk(MathF.Max(value * toMmPerMin, minJerkMmPerMin), setMax,
                            () => axis.Jerk, jerk => axis.Jerk = jerk, jerk => axis.PrintingJerk = jerk);
                    seenAxis = true;
                }
            }

            if (TryGetExtruderValues(code, move, out float[]? extruderValues))
            {
                for (int i = 0; i < extruderValues.Length; i++)
                {
                    Extruder extruder = move.Extruders[i];
                    SetJerk(MathF.Max(extruderValues[i] * toMmPerMin, minJerkMmPerMin), setMax,
                            () => extruder.Jerk, jerk => extruder.Jerk = jerk, jerk => extruder.PrintingJerk = jerk);
                }
                seenExtruder = true;
            }

            if (setMax && code.TryGetInt('P', out int jerkPolicy))
            {
                move.JerkPolicy = jerkPolicy;
                seenAxis = true;
            }

            // An extruder-only M566 reports nothing, matching RepRapFirmware: the report is per axis
            // and would say nothing about what was just set
            if (!seenAxis && !seenExtruder)
            {
                float fromMmPerMin = mmPerSec ? 1.0f / SecondsPerMinute : 1.0f;
                report = ReportPerDrive(move, $"{(setMax ? "Maximum" : "Current")} jerk rates ({(mmPerSec ? "mm/sec" : "mm/min")}): ",
                                        axis => ((setMax ? axis.Jerk : axis.PrintingJerk) * fromMmPerMin).ToString("F1", CultureInfo.InvariantCulture),
                                        extruder => ((setMax ? extruder.Jerk : extruder.PrintingJerk) * fromMmPerMin).ToString("F1", CultureInfo.InvariantCulture));
                if (setMax)
                {
                    report += $", jerk policy: {move.JerkPolicy}";
                }
            }
        }

        if (report is not null)
        {
            return new Message(MessageType.Success, report);
        }

        if (seenAxis && !await planner.ReconfigureAsync(cancellationToken))
        {
            return MotionConfigRejected;
        }
        return new Message();
    }

    /// <summary>
    /// M208: set or report how far each axis may travel
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleAxisLimitsAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        // A lone value is the maximum unless S1 says it is the minimum. Two values are min:max
        bool setMin = code.GetInt('S', 0) == 1;
        bool seen = false;
        string? report = null;

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Move move = model.Move;

            foreach (Axis axis in move.Axes)
            {
                if (!code.TryGetFloatArray(axis.Letter, out float[]? values) || values.Length == 0)
                {
                    continue;
                }
                seen = true;

                float min = axis.Min, max = axis.Max;
                if (values.Length >= 2)
                {
                    min = values[0];
                    max = values[1];
                }
                else if (setMin)
                {
                    min = values[0];
                }
                else
                {
                    max = values[0];
                }

                if (max <= min)
                {
                    return new Message(MessageType.Error, $"{axis.Letter} axis maximum must be greater than minimum");
                }

                axis.Min = min;
                axis.Max = max;
            }

            if (!seen)
            {
                StringBuilder builder = new("Axis limits (mm");
                char separator = ')';
                foreach (Axis axis in move.Axes)
                {
                    builder.Append(CultureInfo.InvariantCulture, $"{separator} {axis.Letter}{axis.Min:F2}:{axis.Max:F2}");
                    separator = ',';
                }
                report = builder.ToString();
            }
        }

        return seen ? new Message() : new Message(MessageType.Success, report!);
    }

    /// <summary>
    /// M350: set or report the microstepping of each drive
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleMicrosteppingAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        bool interpolate = code.GetInt('I', 0) > 0;
        List<RemoteDrivers.DriverValue<(float, int, bool)>> toUpdate = [];
        bool seen = false;
        string? report = null;

        if (SetsAnyDrive(code) && !await FlushAndWaitForStandstillAsync(code, cancellationToken))
        {
            throw new OperationCanceledException();
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Move move = model.Move;

            foreach (Axis axis in move.Axes)
            {
                if (code.TryGetInt(axis.Letter, out int microstepping))
                {
                    axis.Microstepping.Value = microstepping;
                    axis.Microstepping.Interpolated = interpolate;

                    // The position in microsteps no longer means what it did, and nothing has been
                    // measured since, so the axis is no longer known to be where it says it is
                    axis.Homed = false;

                    AddDrivers(toUpdate, axis.Drivers, axis.StepsPerMm, axis.Microstepping);
                    seen = true;
                }
            }

            if (code.TryGetIntArray('E', out int[]? extruderValues) && extruderValues.Length > 0)
            {
                for (int i = 0; i < move.Extruders.Count; i++)
                {
                    int microstepping = extruderValues.Length == 1 ? extruderValues[0]
                                        : i < extruderValues.Length ? extruderValues[i] : int.MinValue;
                    if (microstepping < 0)
                    {
                        // Negative values are how a mixing configuration skips an extruder it does
                        // not want to change
                        continue;
                    }

                    Extruder extruder = move.Extruders[i];
                    extruder.Microstepping.Value = microstepping;
                    extruder.Microstepping.Interpolated = interpolate;
                    AddDriver(toUpdate, extruder.Driver, extruder.StepsPerMm, extruder.Microstepping);
                    seen = true;
                }
            }

            if (!seen)
            {
                report = ReportPerDrive(move, "Microstepping - ",
                                        axis => Describe(axis.Microstepping),
                                        extruder => Describe(extruder.Microstepping),
                                        axisSeparator: ":", extruderHeader: "E", firstExtruderSeparator: ":");
            }
        }

        if (!seen)
        {
            return new Message(MessageType.Success, report!);
        }

        if (!await planner.ReconfigureAsync(cancellationToken))
        {
            return MotionConfigRejected;
        }
        return await UpdateRemoteDriversAsync(toUpdate, cancellationToken);
    }

    /// <summary>
    /// M400: wait for the moves already commanded to finish
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleWaitForMovesAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!await FlushAndWaitForStandstillAsync(code, cancellationToken))
        {
            throw new OperationCanceledException();
        }
        return new Message();
    }

    /// <summary>
    /// M584: map axes and extruders onto stepper drivers, creating axes that do not exist yet
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// This is what brings an axis into existence: <c>move.axes[]</c> starts empty, and an axis
    /// letter named here for the first time adds an entry for it. Nothing can be moved or configured
    /// until that has happened, which is why config.g runs M584 before the rest of the motion setup.
    /// A driver can only drive one axis or extruder, so a mapping that gives one to two drives is
    /// rejected whole and leaves the previous mapping in place. A letter given without drivers gives
    /// up the ones it had, which is the only way to free a driver for another drive to use
    /// </remarks>
    private async ValueTask<Message> HandleDriveMappingAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.Parameters.Count > 0 && !await FlushAndWaitForStandstillAsync(code, cancellationToken))
        {
            throw new OperationCanceledException();
        }

        List<RemoteDrivers.DriverValue<(float, int, bool)>> toUpdate = [];
        List<string> warnings = [];
        bool seen = false;
        string? error = null;

        // R says how a newly created axis wraps and S whether it counts as rotational. Both apply
        // only to axes this code creates; an existing axis keeps the type it was given
        bool seenWrapType = code.TryGetInt('R', out int wrapType);
        bool seenRotational = code.TryGetInt('S', out int rotational);

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Move move = model.Move;

            // Read the whole mapping before writing any of it, because a driver claimed by two
            // drives makes the code an error and the mapping it would have replaced has to survive
            List<(char Letter, DriverId[] Drivers)> axisMapping = [];
            foreach (char letter in Axis.Letters)
            {
                if (TryGetDrivers(code, letter, out DriverId[] drivers))
                {
                    axisMapping.Add((letter, [.. drivers.Where(driver => IsValidDriver(driver, warnings))]));
                }
            }

            DriverId?[]? extruderMapping = null;
            if (TryGetDrivers(code, 'E', out DriverId[] extruderDrivers))
            {
                extruderMapping = [.. extruderDrivers.Select(driver => IsValidDriver(driver, warnings) ? driver : null)];
            }
            seen = axisMapping.Count > 0 || extruderMapping is not null;

            string? conflict = FindDriverConflict(move, axisMapping, extruderMapping);
            if (conflict is not null)
            {
                return new Message(MessageType.Error, conflict);
            }

            foreach ((char letter, DriverId[] drivers) in axisMapping)
            {
                Axis? axis = move.Axes.FirstOrDefault(a => a.Letter == letter);
                if (axis is null)
                {
                    axis = CreateAxis(letter, seenWrapType ? wrapType : null, seenRotational ? rotational == 1 : null);
                    move.Axes.Add(axis);
                }

                axis.Drivers.Clear();
                foreach (DriverId driver in drivers)
                {
                    axis.Drivers.Add(driver);
                }
                AddDrivers(toUpdate, axis.Drivers, axis.StepsPerMm, axis.Microstepping);
            }

            if (extruderMapping is not null)
            {
                // The E list is the whole set of extruders, so one that is no longer named goes away
                while (move.Extruders.Count > extruderMapping.Length)
                {
                    move.Extruders.RemoveAt(move.Extruders.Count - 1);
                }
                while (move.Extruders.Count < extruderMapping.Length)
                {
                    move.Extruders.Add(new Extruder());
                }

                for (int i = 0; i < extruderMapping.Length; i++)
                {
                    Extruder extruder = move.Extruders[i];
                    extruder.Driver = extruderMapping[i];
                    AddDriver(toUpdate, extruder.Driver, extruder.StepsPerMm, extruder.Microstepping);
                }
            }

            if (code.TryGetInt('P', out int visibleAxes))
            {
                seen = true;
                if (visibleAxes < 0 || visibleAxes > move.Axes.Count)
                {
                    error = "Invalid number of visible axes";
                }
                else
                {
                    for (int i = 0; i < move.Axes.Count; i++)
                    {
                        move.Axes[i].Visible = i < visibleAxes;
                    }
                }
            }

            if (!seen)
            {
                return new Message(MessageType.Success, ReportDriveMapping(move));
            }
        }

        if (error is not null)
        {
            return new Message(MessageType.Error, error);
        }

        if (!await planner.ReconfigureAsync(cancellationToken))
        {
            return MotionConfigRejected;
        }

        Message result = await UpdateRemoteDriversAsync(toUpdate, cancellationToken);
        foreach (string warning in warnings)
        {
            result.Append(MessageType.Warning, warning);
        }
        return result;
    }

    /// <summary>
    /// M906, M913 and M917: set or report the motor currents
    /// </summary>
    /// <remarks>
    /// The three differ only in which current they address: M906 the current in mA, M913 that as a
    /// percentage of normal, and M917 the percentage held while the motor is standing still
    /// </remarks>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleMotorCurrentsAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        List<RemoteDrivers.DriverValue<float>> toUpdate = [];
        bool seen = false;
        string? report = null;

        if (SetsAnyDrive(code) && !await FlushAndWaitForStandstillAsync(code, cancellationToken))
        {
            throw new OperationCanceledException();
        }

        int which = code.MajorNumber ?? 906;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Move move = model.Move;

            foreach (Axis axis in move.Axes)
            {
                if (code.TryGetFloat(axis.Letter, out float value))
                {
                    SetCurrent(axis, which, value);
                    foreach (DriverId driver in axis.Drivers)
                    {
                        toUpdate.Add(new RemoteDrivers.DriverValue<float>(driver, CurrentToSend(axis, which)));
                    }
                    seen = true;
                }
            }

            if (TryGetExtruderValues(code, move, out float[]? extruderValues))
            {
                for (int i = 0; i < extruderValues.Length; i++)
                {
                    Extruder extruder = move.Extruders[i];
                    SetCurrent(extruder, which, extruderValues[i]);
                    if (extruder.Driver is not null)
                    {
                        toUpdate.Add(new RemoteDrivers.DriverValue<float>(extruder.Driver, CurrentToSend(extruder, which)));
                    }
                }
                seen = true;
            }

            if (which == 906)
            {
                if (code.TryGetFloatLimited('I', 0.0f, 100.0f, out float idleFactor))
                {
                    move.Idle.Factor = idleFactor / 100.0f;
                    seen = true;
                }
                if (code.TryGetFloat('T', out float idleTimeout))
                {
                    move.Idle.Timeout = MathF.Max(idleTimeout, 0.0f);
                    seen = true;
                }
            }

            if (!seen)
            {
                string prefix = which switch
                {
                    913 => "Motor current % of normal - ",
                    917 => "Motor standstill current % of normal - ",
                    _ => "Motor current (mA) - "
                };
                report = ReportPerDrive(move, prefix,
                                        axis => CurrentOf(axis, which).ToString(CultureInfo.InvariantCulture),
                                        extruder => CurrentOf(extruder, which).ToString(CultureInfo.InvariantCulture),
                                        axisSeparator: ":", extruderHeader: "E", firstExtruderSeparator: ":");
                if (which == 906)
                {
                    report += string.Format(CultureInfo.InvariantCulture, ", idle factor {0}%, timeout {1:F1} sec",
                                            (int)(move.Idle.Factor * 100.0f), move.Idle.Timeout);
                }
            }
        }

        if (!seen)
        {
            return new Message(MessageType.Success, report!);
        }

        // The standstill percentage is its own setting on the driver; the other two both end up as
        // the current the driver should run at
        IList<Message> replies = which == 917
            ? await RemoteDrivers.SetStandstillCurrentFactorAsync(linkInterface, toUpdate, cancellationToken)
            : await RemoteDrivers.SetMotorCurrentsAsync(linkInterface, toUpdate, cancellationToken);
        return replies.ToMessage();
    }

    /// <summary>
    /// M17, M18 and M84: energise or de-energise the motors, and set the idle timeout
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// Naming no drive applies the code to every drive, which is what makes a bare M18 turn the whole
    /// machine off. A de-energised axis is no longer known to be where it says it is, so it stops
    /// counting as homed
    /// </remarks>
    private async ValueTask<Message> HandleDriverStateAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        bool enable = code.MajorNumber == 17;
        List<RemoteDrivers.DriverValue<(ushort, ushort)>> toUpdate = [];

        if (!await FlushAndWaitForStandstillAsync(code, cancellationToken))
        {
            throw new OperationCanceledException();
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Move move = model.Move;

            if (code.TryGetFloat('S', out float idleTimeout))
            {
                move.Idle.Timeout = MathF.Max(idleTimeout, 0.0f);
            }

            ushort mode = enable ? DriverStateControl.DriverActive : DriverStateControl.DriverDisabled;
            ushort idlePercent = (ushort)Math.Clamp((int)MathF.Round(move.Idle.Factor * 100.0f), 0, 100);

            bool named = false;
            foreach (Axis axis in move.Axes)
            {
                if (code.HasParameter(axis.Letter))
                {
                    named = true;
                    ApplyDriverState(toUpdate, axis.Drivers, mode, idlePercent);
                    if (!enable)
                    {
                        axis.Homed = false;
                    }
                }
            }

            if (code.TryGetIntArray('E', out int[]? extruders) && extruders.Length > 0)
            {
                named = true;
                foreach (int extruder in extruders)
                {
                    if (extruder < 0 || extruder >= move.Extruders.Count)
                    {
                        return new Message(MessageType.Error, $"Invalid extruder number specified: {extruder}");
                    }
                    AddDriverState(toUpdate, move.Extruders[extruder].Driver, mode, idlePercent);
                }
            }

            if (!named)
            {
                // No drive named, so this is about all of them
                foreach (Axis axis in move.Axes)
                {
                    ApplyDriverState(toUpdate, axis.Drivers, mode, idlePercent);
                    if (!enable)
                    {
                        axis.Homed = false;
                    }
                }
                foreach (Extruder extruder in move.Extruders)
                {
                    AddDriverState(toUpdate, extruder.Driver, mode, idlePercent);
                }
            }
        }

        if (toUpdate.Count == 0)
        {
            return new Message();
        }

        IList<Message> replies = await RemoteDrivers.SetDriverStatesAsync(linkInterface, toUpdate, cancellationToken);
        return replies.ToMessage();
    }

    /// <summary>
    /// M85: set the idle timeout
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleIdleTimeoutAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (!code.TryGetFloat('S', out float timeout))
            {
                return new Message(MessageType.Success,
                                   string.Format(CultureInfo.InvariantCulture, "Idle timeout {0:F1} sec", model.Move.Idle.Timeout));
            }
            model.Move.Idle.Timeout = MathF.Max(timeout, 0.0f);
        }
        return new Message();
    }

    /// <summary>
    /// M569: configure a stepper driver
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// Every parameter of this code belongs to the driver rather than to this side, so the whole code
    /// is repackaged into the CAN message its table describes and answered by the board that owns the
    /// driver. The sub-codes are separate message types over the same mechanism
    /// </remarks>
    private async ValueTask<Message> HandleDriverConfigAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!code.TryGetDriverId('P', out DriverId? driver))
        {
            return new Message(MessageType.Error, "Missing P parameter");
        }

        if (CanAddresses.HasNoHardware(driver.Board))
        {
            // Nothing there would answer, and the code would sit out its timeout before saying so
            return new Message(MessageType.Error, CanAddresses.NoHardwareMessage($"Driver {driver}"));
        }

        if (!await FlushAndWaitForStandstillAsync(code, cancellationToken))
        {
            throw new OperationCanceledException();
        }

        CanResponse response;
        response = code.MinorNumber switch
        {
            <= 0 => await SendDriverConfigAsync<CanMessageM569>(driver, code, cancellationToken),
            1 => await SendDriverConfigAsync<CanMessageM569Point1>(driver, code, cancellationToken),
            2 => await SendDriverConfigAsync<CanMessageM569Point2>(driver, code, cancellationToken),
            4 => await SendDriverConfigAsync<CanMessageM569Point4>(driver, code, cancellationToken),
            6 => await SendDriverConfigAsync<CanMessageM569Point6>(driver, code, cancellationToken),
            7 => await SendDriverConfigAsync<CanMessageM569Point7>(driver, code, cancellationToken),
            _ => throw new NotSupportedException($"M569.{code.MinorNumber} is not supported")
        };

        if (code.MinorNumber <= 0)
        {
            await RecordDriverConfigAsync(driver, code, cancellationToken);
        }
        return response.ToMessage();
    }

    /// <summary>
    /// Record what M569 just set on a driver
    /// </summary>
    /// <param name="driver">The driver</param>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// Only the parameters the code actually carried are written, so a code that sets one thing does
    /// not reset the rest to their defaults
    /// </remarks>
    private async ValueTask RecordDriverConfigAsync(DriverId driver, Commands.Code code, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            DriverConfig config = GetOrCreateDriver(driver).Config;

            if (code.TryGetInt('S', out int direction))
            {
                config.Direction = direction != 0;
            }
            if (code.TryGetInt('R', out int enablePolarity))
            {
                config.EnablePolarity = enablePolarity;
            }
            if (code.TryGetInt('D', out int mode))
            {
                config.Mode = Enum.IsDefined((DriverMode)mode) ? (DriverMode)mode : DriverMode.Unknown;
            }
            if (code.TryGetInt('F', out int offTime))
            {
                config.OffTime = offTime;
            }
            if (code.TryGetInt('B', out int blankingTime))
            {
                config.BlankingTime = blankingTime;
            }
            if (code.TryGetInt('V', out int stealthChopThreshold))
            {
                config.StealthChopThreshold = stealthChopThreshold;
            }
            if (code.TryGetInt('H', out int coolStepThreshold))
            {
                config.CoolStepThreshold = coolStepThreshold;
            }
            if (code.TryGetInt('U', out int currentScaler))
            {
                config.CurrentScaler = currentScaler;
            }
            if (code.TryGetIntArray('Y', out int[]? hysteresis) && hysteresis.Length > 0)
            {
                config.Hysteresis.Start = hysteresis[0];
                if (hysteresis.Length > 1)
                {
                    config.Hysteresis.End = hysteresis[1];
                }
                if (hysteresis.Length > 2)
                {
                    config.Hysteresis.Decrement = hysteresis[2];
                }
            }
            if (code.TryGetFloatArray('T', out float[]? timings) && timings.Length > 0)
            {
                // A single value sets all four timings, which is how most configurations are written
                config.StepTiming.Clear();
                for (int i = 0; i < 4; i++)
                {
                    config.StepTiming.Add(timings.Length == 1 ? timings[0] : i < timings.Length ? timings[i] : 0.0f);
                }
            }
        }
    }

    /// <summary>
    /// Find a driver in the object model, adding the board and the driver if they are not there yet
    /// </summary>
    /// <param name="driver">The driver</param>
    /// <returns>The driver's object model entry</returns>
    /// <remarks>
    /// config.g configures drivers before the boards carrying them have necessarily announced
    /// themselves, so the entry has to be created on demand rather than waited for
    /// </remarks>
    private Driver GetOrCreateDriver(DriverId driver)
    {
        Board? board = model.Boards.FirstOrDefault(b => b.CanAddress == driver.Board);
        if (board is null)
        {
            board = new Board { CanAddress = driver.Board };
            model.Boards.Add(board);
        }

        board.Drivers ??= [];
        while (board.Drivers.Count <= driver.Port)
        {
            board.Drivers.Add(new Driver());
        }
        return board.Drivers[driver.Port];
    }

    /// <summary>
    /// Repackage a code as a generic CAN message and send it to the board carrying the driver
    /// </summary>
    /// <typeparam name="TMessage">Type of the CAN message</typeparam>
    /// <param name="driver">Driver the code addresses</param>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the board replied</returns>
    /// <remarks>
    /// A <see cref="DriverId"/> is a board and a port on it, and only the board half addresses a
    /// message, so this is where that is said once rather than at each of the M569 sub-codes
    /// </remarks>
    private ValueTask<CanResponse> SendDriverConfigAsync<TMessage>(DriverId driver, Commands.Code code,
                                                                   CancellationToken cancellationToken)
        where TMessage : struct, ICanGenericMessage<TMessage>
        => new(linkInterface.SendCodeAsync<TMessage>((byte)driver.Board, code, cancellationToken: cancellationToken));

    /// <summary>
    /// M915: configure stall detection
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// The drivers may be named directly with P or by the axes they belong to, and either way they
    /// have to be grouped by the board that carries them before the message can go out
    /// </remarks>
    private async ValueTask<Message> HandleStallDetectionAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        List<DriverId> drivers = [];

        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (code.TryGetDriverIdArray('P', out DriverId[]? named))
            {
                foreach (DriverId driver in named)
                {
                    if (CanAddresses.HasNoHardware(driver.Board))
                    {
                        return new Message(MessageType.Error, CanAddresses.NoHardwareMessage($"Driver {driver}"));
                    }
                    drivers.Add(driver);
                }
            }

            foreach (Axis axis in model.Move.Axes)
            {
                if (code.HasParameter(axis.Letter))
                {
                    drivers.AddRange(axis.Drivers);
                }
            }
        }

        if (drivers.Count == 0)
        {
            return new Message(MessageType.Error, "No drivers specified");
        }

        List<Message> replies = [];
        foreach (IGrouping<int, DriverId> board in drivers.GroupBy(driver => driver.Board))
        {
            CanMessageM915 message = default;
            try
            {
                message.FromCode(code);
            }
            catch (CanGenericParamException e)
            {
                return new Message(MessageType.Error, e.Message);
            }

            // 'd' is lowercase in the table so that it is never taken from the code: it is the bitmap
            // of the board's own driver numbers, which only this side can work out
            ushort bitmap = 0;
            foreach (DriverId driver in board)
            {
                bitmap |= (ushort)(1 << driver.Port);
            }
            message.d = bitmap;

            CanResponse response = await linkInterface.SendCanMessageAsync((byte)board.Key, in message, CanMessageType.StandardReply,
                                                                           cancellationToken: cancellationToken);
            replies.Add(response.ToMessage());
        }

        await RecordStallDetectionAsync(drivers, code, cancellationToken);
        return replies.ToMessage();
    }

    /// <summary>
    /// Record what M915 just set on a set of drivers
    /// </summary>
    /// <param name="drivers">Drivers the code addressed</param>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async ValueTask RecordStallDetectionAsync(IEnumerable<DriverId> drivers, Commands.Code code, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            foreach (DriverId driver in drivers)
            {
                DriverStallDetection stall = GetOrCreateDriver(driver).Config.StallDetection;

                if (code.TryGetInt('S', out int threshold))
                {
                    stall.Threshold = threshold;
                }
                if (code.TryGetInt('F', out int filter))
                {
                    stall.Filter = filter != 0;
                }
                if (code.TryGetInt('H', out int minimumSpeed))
                {
                    stall.MinimumSpeed = minimumSpeed;
                }
                if (code.TryGetInt('T', out int coolStep))
                {
                    stall.CoolStep = coolStep;
                }
                if (code.TryGetInt('R', out int raiseEvent))
                {
                    stall.RaiseEvent = raiseEvent != 0;
                }
            }
        }
    }

    /// <summary>
    /// M970: configure phase stepping
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// Phase stepping drives the motor coils directly from the main board and cannot be done over
    /// CAN, which RepRapFirmware enforces by refusing the mode for any axis with a remote driver.
    /// Every driver is remote here, so there is nothing this can ever do
    /// </remarks>
    private ValueTask<Message> HandlePhaseSteppingAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        _ = code;
        _ = cancellationToken;
        return ValueTask.FromResult(new Message(MessageType.Error,
            "Phase stepping is not supported on CAN-connected drivers"));
    }

    /// <summary>
    /// M572: set or report the pressure advance of each extruder
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandlePressureAdvanceAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!code.TryGetFloatArray('S', out float[]? given) || given.Length == 0)
        {
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                StringBuilder report = new("Pressure advance ");
                string separator = string.Empty;
                foreach (Extruder extruder in model.Move.Extruders)
                {
                    report.Append(separator).Append(extruder.PressAdv.K0.ToString("F3", CultureInfo.InvariantCulture));
                    separator = ":";
                }
                return new Message(MessageType.Success, report.Append(" sec").ToString());
            }
        }

        // S may carry a second coefficient, which applies above the extrusion speed named by L
        float k0 = given[0], k1 = given.Length > 1 ? given[1] : given[0];
        if (k0 < 0.0f || k1 < 0.0f)
        {
            return new Message(MessageType.Error, "pressure advance values must be non-negative");
        }

        float? dk = null;
        if (given.Length > 1)
        {
            if (!code.TryGetFloat('L', out float transition) || transition < 0.0f)
            {
                return new Message(MessageType.Error, "a second pressure advance coefficient needs a non-negative L parameter");
            }
            dk = transition;
        }

        if (!await FlushAndWaitForStandstillAsync(code, cancellationToken))
        {
            throw new OperationCanceledException();
        }

        List<RemoteDrivers.DriverValue<float>> toUpdate = [];
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Move move = model.Move;

            // D names the extruders to change; without it every extruder is set
            bool hasSelection = code.TryGetIntArray('D', out int[]? selected);
            for (int extruder = 0; extruder < move.Extruders.Count; extruder++)
            {
                if (hasSelection && !selected!.Contains(extruder))
                {
                    continue;
                }

                Extruder e = move.Extruders[extruder];
                e.PressAdv.K0 = k0;
                e.PressAdv.K1 = k1;
                e.PressAdv.D = dk;
                if (e.Driver is not null)
                {
                    // The message carries the first coefficient; the second and its transition point
                    // are held here until the wider pressure advance message is ported
                    toUpdate.Add(new RemoteDrivers.DriverValue<float>(e.Driver, k0));
                }
            }
        }

        if (!await planner.ReconfigureAsync(cancellationToken))
        {
            return MotionConfigRejected;
        }

        if (toUpdate.Count == 0)
        {
            return new Message();
        }

        IList<Message> replies = await RemoteDrivers.SetPressureAdvanceAsync(linkInterface, toUpdate, cancellationToken);
        return replies.ToMessage();
    }

    /// <summary>
    /// M592: configure nonlinear extrusion
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleNonlinearExtrusionAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!code.TryGetInt('D', out int extruderNumber))
        {
            return new Message(MessageType.Error, "Missing D parameter");
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (extruderNumber < 0 || extruderNumber >= model.Move.Extruders.Count)
            {
                return new Message(MessageType.Error, $"Invalid extruder number '{extruderNumber}'");
            }

            ExtruderNonlinear nonlinear = model.Move.Extruders[extruderNumber].Nonlinear;
            bool seen = false;
            if (code.TryGetFloat('A', out float a))
            {
                nonlinear.A = a;
                seen = true;
            }
            if (code.TryGetFloat('B', out float b))
            {
                nonlinear.B = b;
                seen = true;
            }
            if (code.TryGetFloat('L', out float limit))
            {
                nonlinear.UpperLimit = limit;
                seen = true;
            }

            if (!seen)
            {
                return new Message(MessageType.Success,
                                   string.Format(CultureInfo.InvariantCulture,
                                                 "Extruder {0} nonlinear extrusion A={1:F3} B={2:F3}, limit {3:F2}",
                                                 extruderNumber, nonlinear.A, nonlinear.B, nonlinear.UpperLimit));
            }
        }
        return new Message();
    }

    /// <summary>
    /// M593: configure input shaping
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// The shaper's impulses are computed by the motion engine from the type, frequency and damping,
    /// so what this writes is the configuration rather than the impulses themselves
    /// </remarks>
    private async ValueTask<Message> HandleInputShapingAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        bool seen = false;
        string? report = null;

        if (!await FlushAndWaitForStandstillAsync(code, cancellationToken))
        {
            throw new OperationCanceledException();
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            InputShaping shaping = model.Move.Shaping;

            if (code.TryGetString('P', out string? typeName))
            {
                if (!Enum.TryParse(typeName, true, out InputShapingType type))
                {
                    return new Message(MessageType.Error, $"Unknown input shaper type '{typeName}'");
                }
                shaping.Type = type;
                seen = true;
            }
            if (code.TryGetFloatLimited('F', MinShapingFrequency, MaxShapingFrequency, out float frequency))
            {
                shaping.Frequency = frequency;
                seen = true;
            }
            if (code.TryGetFloatLimited('S', 0.0f, 0.99f, out float damping))
            {
                shaping.Damping = damping;
                seen = true;
            }

            // Naming a frequency or damping without a type is how a shaper gets switched on
            if (seen && shaping.Type == InputShapingType.None)
            {
                shaping.Type = InputShapingType.ZVD;
            }

            if (!seen)
            {
                report = shaping.Type == InputShapingType.None
                    ? "Input shaping is disabled"
                    : string.Format(CultureInfo.InvariantCulture, "Input shaping '{0}' at {1:F1}Hz damping factor {2:F2}",
                                    shaping.Type.ToString().ToLowerInvariant(), shaping.Frequency, shaping.Damping);
            }
        }

        if (!seen)
        {
            return new Message(MessageType.Success, report!);
        }

        return await planner.ReconfigureAsync(cancellationToken) ? new Message() : MotionConfigRejected;
    }

    /// <summary>
    /// M82 and M83: whether extruder coordinates are absolute or relative
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// This overrides what G90 and G91 set for the extruders only, which is why it is a setting of
    /// its own rather than a synonym
    /// </remarks>
    private async ValueTask<Message> HandleExtruderPositioningAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            InputChannel? input = model.Inputs[code.Channel];
            if (input is null)
            {
                return new Message(MessageType.Error, $"Unknown code channel {code.Channel}");
            }
            input.DrivesRelative = code.MajorNumber == 83;
        }
        return new Message();
    }

    /// <summary>
    /// M114: report where the machine is
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// The shape is RepRapFirmware's, which is in turn Marlin's with the machine coordinates and step
    /// counts appended. There is deliberately no space after each axis colon: Pronterface misparses
    /// the reply if there is one
    /// </remarks>
    private async ValueTask<Message> HandleReportPositionAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        _ = code;
        StringBuilder builder = new();

        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            Move move = model.Move;

            foreach (Axis axis in move.Axes)
            {
                if (axis.Visible)
                {
                    builder.Append(CultureInfo.InvariantCulture, $"{axis.Letter}:{axis.UserPosition:F3} ");
                }
            }

            // The virtual extruder position, which is what OctoPrint reads
            float virtualEPos = move.MotionSystems.Count > 0 ? move.MotionSystems[0].VirtualEPos : 0.0f;
            builder.Append(CultureInfo.InvariantCulture, $"E:{virtualEPos:F3} ");

            for (int extruder = 0; extruder < move.Extruders.Count; extruder++)
            {
                builder.Append(CultureInfo.InvariantCulture, $"E{extruder}:{move.Extruders[extruder].Position:F1} ");
            }

            builder.Append("Count");
            foreach (Axis axis in move.Axes)
            {
                if (axis.Visible)
                {
                    builder.Append(CultureInfo.InvariantCulture, $" {axis.StepPos}");
                }
            }

            builder.Append(" Machine");
            foreach (Axis axis in move.Axes)
            {
                if (axis.Visible)
                {
                    builder.Append(CultureInfo.InvariantCulture, $" {axis.MachinePosition ?? 0.0f:F3}");
                }
            }
        }
        return new Message(MessageType.Success, builder.ToString());
    }

    /// <summary>
    /// M120 and M121: save and restore the interpreter state of this channel
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleStateStackAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            InputChannel? input = model.Inputs[code.Channel];
            if (input is null)
            {
                return new Message(MessageType.Error, $"Unknown code channel {code.Channel}");
            }

            if (code.MajorNumber == 120)
            {
                if (!stateStack.TryPush(code.Channel, input))
                {
                    return new Message(MessageType.Error, "Push(): stack overflow");
                }
            }
            else if (!stateStack.TryPop(code.Channel, input))
            {
                return new Message(MessageType.Error, "Pop(): stack underflow");
            }
        }
        return new Message();
    }

    /// <summary>
    /// M220: set or report the speed factor applied to every move
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleSpeedFactorAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (!code.TryGetFloat('S', out float percentage))
            {
                return new Message(MessageType.Success,
                                   string.Format(CultureInfo.InvariantCulture, "Speed factor: {0:F1}%", model.Move.SpeedFactor * 100.0f));
            }

            float factor = percentage * 0.01f;
            if (factor < MinOverrideFactor)
            {
                return new Message(MessageType.Error, "Invalid speed factor");
            }
            model.Move.SpeedFactor = factor;
        }
        return new Message();
    }

    /// <summary>
    /// M221: set or report the extrusion factor
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// Without D the code applies to the extruders of the current tool, which needs a tool subsystem.
    /// Until then D is required rather than silently applying to everything, which would be worse
    /// than saying so
    /// </remarks>
    private async ValueTask<Message> HandleExtrusionFactorAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (!code.TryGetInt('D', out int extruderNumber))
            {
                return new Message(MessageType.Error, "No tool selected");
            }
            if (extruderNumber < 0 || extruderNumber >= model.Move.Extruders.Count)
            {
                return new Message(MessageType.Error, $"Invalid extruder number '{extruderNumber}'");
            }

            Extruder extruder = model.Move.Extruders[extruderNumber];
            if (!code.TryGetFloat('S', out float percentage))
            {
                return new Message(MessageType.Success,
                                   string.Format(CultureInfo.InvariantCulture, "Extrusion factor for extruder {0}: {1:F1}%",
                                                 extruderNumber, extruder.Factor * 100.0f));
            }

            float factor = percentage * 0.01f;
            if (factor >= MinOverrideFactor)
            {
                extruder.Factor = factor;
            }
        }
        return new Message();
    }

    /// <summary>
    /// M290: babystepping
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// The offset is applied to every move built afterwards rather than by moving now, which is what
    /// lets it be adjusted while a print is running. S is a synonym for Z, and R0 makes the values
    /// absolute rather than a change to what is already applied
    /// </remarks>
    private async ValueTask<Message> HandleBabysteppingAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        bool absolute = code.GetInt('R', 1) == 0;
        bool seen = false;
        string? report = null;

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Move move = model.Move;

            for (int index = 0; index < move.Axes.Count; index++)
            {
                Axis axis = move.Axes[index];

                // S has meant "the Z axis" since before M290 took axis letters
                bool haveValue = code.TryGetFloat(axis.Letter, out float value);
                if (!haveValue && index == 2)
                {
                    haveValue = code.TryGetFloat('S', out value);
                }
                if (!haveValue)
                {
                    continue;
                }

                axis.Babystep = absolute ? value : axis.Babystep + Math.Clamp(value, -MaxRelativeBabystep, MaxRelativeBabystep);
                seen = true;
            }

            // TODO push new babystep values into the DDARing so they happen ASAP rather than waiting for the next move to be built

            if (!seen)
            {
                StringBuilder builder = new("Baby stepping offsets (mm):");
                foreach (Axis axis in move.Axes)
                {
                    builder.Append(CultureInfo.InvariantCulture, $" {axis.Letter}:{axis.Babystep:F3}");
                }
                report = builder.ToString();
            }
        }

        if (!seen)
        {
            return new Message(MessageType.Success, report!);
        }

        return await planner.ReconfigureAsync(cancellationToken) ? new Message() : MotionConfigRejected;
    }

    /// <summary>
    /// M425: configure backlash compensation
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleBacklashAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        bool seen = false;
        string? report = null;

        if (!await FlushAndWaitForStandstillAsync(code, cancellationToken))
        {
            throw new OperationCanceledException();
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Move move = model.Move;

            foreach (Axis axis in move.Axes)
            {
                if (code.TryGetFloat(axis.Letter, out float backlash))
                {
                    axis.Backlash = MathF.Max(backlash, 0.0f);
                    seen = true;
                }
            }

            if (code.TryGetIntLimited('S', 1, 100, out int factor))
            {
                move.BacklashFactor = factor;
                seen = true;
            }

            if (!seen)
            {
                StringBuilder builder = new("Backlash correction (mm)");
                foreach (Axis axis in move.Axes)
                {
                    builder.Append(CultureInfo.InvariantCulture, $" {axis.Letter}: {axis.Backlash:F3}");
                }
                builder.Append(CultureInfo.InvariantCulture, $", correction distance multiplier {move.BacklashFactor}");
                report = builder.ToString();
            }
        }

        if (!seen)
        {
            return new Message(MessageType.Success, report!);
        }

        return await planner.ReconfigureAsync(cancellationToken) ? new Message() : MotionConfigRejected;
    }

    /// <summary>
    /// M556: configure axis skew compensation
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// The values given are deviations measured over the distance S, and what is stored is the
    /// tangent of the resulting angle. X is the XY skew, Y the YZ skew and Z the XZ skew
    /// </remarks>
    private async ValueTask<Message> HandleAxisCompensationAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        bool seen = false;
        string? report = null;

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Skew skew = model.Move.Compensation.Skew;

            if (code.TryGetFloat('S', out float measuredOver))
            {
                if (measuredOver <= 0.0f)
                {
                    return new Message(MessageType.Error, "S parameter must be greater than zero");
                }

                if (code.TryGetFloat('X', out float xDeviation))
                {
                    skew.TanXY = xDeviation / measuredOver;
                    seen = true;
                }
                if (code.TryGetFloat('Y', out float yDeviation))
                {
                    skew.TanYZ = yDeviation / measuredOver;
                    seen = true;
                }
                if (code.TryGetFloat('Z', out float zDeviation))
                {
                    skew.TanXZ = zDeviation / measuredOver;
                    seen = true;
                }
            }

            if (code.TryGetInt('P', out int compensateXY))
            {
                skew.CompensateXY = compensateXY <= 0;
                seen = true;
            }

            if (!seen)
            {
                report = string.Format(CultureInfo.InvariantCulture,
                                       "Axis compensations - {0}: {1:F5}, YZ: {2:F5}, ZX: {3:F5}",
                                       skew.CompensateXY ? "XY" : "YX", skew.TanXY, skew.TanYZ, skew.TanXZ);
            }
        }

        return seen ? new Message() : new Message(MessageType.Success, report!);
    }

    /// <summary>
    /// M564: whether moves are limited to the axis travel and whether they are allowed before homing
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleMovementLimitsAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        bool seen = false;
        string? report = null;

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Move move = model.Move;

            if (code.TryGetInt('S', out int limitAxes))
            {
                move.LimitAxes = limitAxes > 0;
                seen = true;
            }
            if (code.TryGetInt('H', out int noMovesBeforeHoming))
            {
                move.NoMovesBeforeHoming = noMovesBeforeHoming > 0;
                seen = true;
            }

            if (!seen)
            {
                report = string.Format("Movement outside the bed is {0}permitted, movement before homing is {1}permitted",
                                       move.LimitAxes ? "not " : string.Empty,
                                       move.NoMovesBeforeHoming ? "not " : string.Empty);
            }
        }

        if (!seen)
        {
            return new Message(MessageType.Success, report!);
        }

        return await planner.ReconfigureAsync(cancellationToken) ? new Message() : MotionConfigRejected;
    }

    /// <summary>
    /// M665, M666 and M669: select the machine's geometry and configure it
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// <para>
    /// One handler for all three, because all three do the same thing to the same object: M669
    /// selects a geometry and configures whichever one is selected, M665 configures a delta's
    /// geometry, and M666 configures its corrections. Which parameters mean what is the geometry's
    /// business rather than this handler's - see
    /// <see cref="Motion.Kinematics.KinematicsConfigurator"/>.
    /// </para>
    /// <para>
    /// The geometry is configured first and the object model written from it, which is the direction
    /// §14 of <c>docs/devel/MCODE_MIGRATION.md</c> establishes. Configuring takes no locks, so the
    /// model's write lock is held only for the projection - and the planner lock is taken inside it,
    /// which is the order §13.1 fixes
    /// </para>
    /// </remarks>
    private async ValueTask<Message> HandleKinematicsAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!await FlushAndWaitForStandstillAsync(code, cancellationToken))
        {
            throw new OperationCanceledException();
        }

        int mCode = code.MajorNumber!.Value;
        bool seen = false;
        KinematicsEngine geometry = KinematicsConfigurator.Apply(planner.Geometry, code, ref seen);

        if (!seen)
        {
            // Nothing to configure, so the code is asking rather than telling. A geometry that the
            // code does not apply to says so, and that is an error rather than a report
            StringBuilder builder = new();
            bool applies = geometry.AppendReport(builder, mCode);
            return new Message(applies ? MessageType.Success : MessageType.Error, builder.ToString());
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            planner.SetGeometry(geometry);
            KinematicsConfigurator.WriteTo(geometry, model.Move);

            // The geometry moved underneath the machine, so nothing is where it was
            foreach (Axis axis in model.Move.Axes)
            {
                axis.Homed = false;
            }
        }

        return await planner.ReconfigureAsync(cancellationToken) ? new Message() : MotionConfigRejected;
    }

    /// <summary>
    /// M671: set the positions of the Z leadscrews or bed levelling screws
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleLeadscrewsAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        bool seen = false;
        string? report = null;

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (model.Move.Kinematics is not ZLeadscrewKinematics leadscrewKinematics)
            {
                return new Message(MessageType.Error, "M671 is not applicable to this kinematics");
            }

            TiltCorrection tilt = leadscrewKinematics.TiltCorrection;

            if (code.TryGetFloatArray('X', out float[]? screwX))
            {
                tilt.ScrewX.Clear();
                foreach (float x in screwX)
                {
                    tilt.ScrewX.Add(x);
                }
                seen = true;
            }
            if (code.TryGetFloatArray('Y', out float[]? screwY))
            {
                tilt.ScrewY.Clear();
                foreach (float y in screwY)
                {
                    tilt.ScrewY.Add(y);
                }
                seen = true;
            }

            if (seen && tilt.ScrewX.Count != tilt.ScrewY.Count)
            {
                return new Message(MessageType.Error, "M671: must have the same number of X and Y coordinates");
            }

            if (code.TryGetFloat('S', out float maxCorrection))
            {
                tilt.MaxCorrection = maxCorrection;
                seen = true;
            }
            if (code.TryGetFloat('P', out float screwPitch))
            {
                tilt.ScrewPitch = screwPitch;
                seen = true;
            }
            if (code.TryGetFloat('F', out float correctionFactor))
            {
                tilt.CorrectionFactor = correctionFactor;
                seen = true;
            }

            if (!seen)
            {
                StringBuilder builder = new("Leadscrew/levelling screw coordinates");
                for (int screw = 0; screw < tilt.ScrewX.Count; screw++)
                {
                    builder.Append(CultureInfo.InvariantCulture, $" {tilt.ScrewX[screw]:F1},{tilt.ScrewY[screw]:F1}");
                }
                builder.Append(CultureInfo.InvariantCulture,
                               $", maximum correction {tilt.MaxCorrection:F2}mm, manual adjusting screw pitch {tilt.ScrewPitch:F2}mm");
                report = builder.ToString();
            }
        }

        if (!seen)
        {
            return new Message(MessageType.Success, report!);
        }

        return await planner.ReconfigureAsync(cancellationToken) ? new Message() : MotionConfigRejected;
    }

    /// <summary>
    /// M574: configure the endstops
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// Each axis letter carries where its endstop is - 0 none, 1 low end, 2 high end - S says what
    /// kind of input it is, and P names the port. The board carrying that port is asked to watch it
    /// and to report changes, which is what turns an endstop into something a move can stop on
    /// </remarks>
    private async ValueTask<Message> HandleEndstopConfigAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        // S defaults to a switch on an input pin, which is what almost every endstop is
        int inputType = code.GetInt('S', (int)RrfEndstopType.InputPin);
        if (!Enum.IsDefined((RrfEndstopType)inputType))
        {
            return new Message(MessageType.Error, "Invalid endstop input type");
        }

        List<(int Axis, EndstopPosition Position)> configured = [];
        string? report = null;

        if (!await FlushAndWaitForStandstillAsync(code, cancellationToken))
        {
            throw new OperationCanceledException();
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Move move = model.Move;

            for (int axis = 0; axis < move.Axes.Count; axis++)
            {
                if (code.TryGetInt(move.Axes[axis].Letter, out int position))
                {
                    if (!Enum.IsDefined((EndstopPosition)position))
                    {
                        return new Message(MessageType.Error, "Invalid endstop position");
                    }
                    configured.Add((axis, (EndstopPosition)position));
                }
            }

            if (configured.Count == 0)
            {
                StringBuilder builder = new("Endstop configuration:");
                for (int axis = 0; axis < move.Axes.Count; axis++)
                {
                    builder.Append(CultureInfo.InvariantCulture, $"\n{move.Axes[axis].Letter}: ");
                    Endstop? endstop = axis < model.Sensors.Endstops.Count ? model.Sensors.Endstops[axis] : null;
                    builder.Append(endstop is null
                                   ? "none"
                                   : $"{(endstop.HighEnd ? "high end" : "low end")} {DescribeEndstop(endstop)}");
                }
                report = builder.ToString();
            }
            else
            {
                // A port can only be named for one axis at a time, because it names one input
                bool hasPort = code.TryGetString('P', out string? port);
                if (hasPort && (configured.Count > 1 || inputType != (int)RrfEndstopType.InputPin))
                {
                    return new Message(MessageType.Error, "Invalid use of P parameter");
                }

                if (hasPort && ValidateEndstopPorts(port!, move.Axes[configured[0].Axis]) is string portError)
                {
                    return new Message(MessageType.Error, portError);
                }

                foreach ((int axis, EndstopPosition position) in configured)
                {
                    if (position == EndstopPosition.None)
                    {
                        // Removing an endstop leaves the slot empty rather than absent, so the
                        // collection stays indexed by axis
                        if (axis < model.Sensors.Endstops.Count)
                        {
                            model.Sensors.Endstops[axis] = null;
                        }
                        continue;
                    }

                    Endstop endstop = GetOrCreateEndstop(axis);
                    endstop.HighEnd = position == EndstopPosition.HighEnd;
                    endstop.Type = ToEndstopType((RrfEndstopType)inputType);
                    if (hasPort)
                    {
                        endstop.Port = port;
                    }
                }
            }
        }

        if (report is not null)
        {
            return new Message(MessageType.Success, report);
        }

        // Tell the boards to watch the ports. Done outside the model lock because it goes over CAN
        List<Message> replies = [];
        foreach ((int axis, EndstopPosition position) in configured)
        {
            if (position == EndstopPosition.None)
            {
                continue;
            }

            replies.Add(await CreateEndstopMonitorAsync(axis, cancellationToken));
        }

        return replies.ToMessage();
    }

    /// <summary>
    /// M119: report which endstops are triggered
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleReportEndstopsAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        _ = code;
        StringBuilder builder = new("Endstops - ");

        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            for (int axis = 0; axis < model.Move.Axes.Count; axis++)
            {
                Endstop? endstop = axis < model.Sensors.Endstops.Count ? model.Sensors.Endstops[axis] : null;
                string state = endstop is null ? "no endstop" : endstop.Triggered ? "at min stop" : "not stopped";
                builder.Append(CultureInfo.InvariantCulture, $"{model.Move.Axes[axis].Letter}: {state}, ");
            }
            // RepRapFirmware reports the currently selected probe here; there is only ever probe 0
            // to select until G30 exists to select another
            Probe? probe = model.Sensors.Probes.Count > 0 ? model.Sensors.Probes[0] : null;
            builder.Append("Z probe: ").Append(DescribeProbeState(probe));
        }
        return new Message(MessageType.Success, builder.ToString());
    }

    #region Helpers

    /// <summary>
    /// Where an endstop sits, as the M574 axis parameter spells it
    /// </summary>
    private enum EndstopPosition
    {
        /// <summary>The axis has no endstop</summary>
        None = 0,

        /// <summary>At the low end of the axis</summary>
        LowEnd = 1,

        /// <summary>At the high end of the axis</summary>
        HighEnd = 2
    }

    /// <summary>
    /// What kind of input an endstop is, as the M574 S parameter spells it
    /// </summary>
    /// <remarks>
    /// The numbering is RepRapFirmware's and is part of the interface a config.g depends on, so it
    /// is spelled out rather than derived from the object model's own enum, which is ordered
    /// differently and has no equivalent of the retired first entry
    /// </remarks>
    private enum RrfEndstopType
    {
        /// <summary>Retired: used to select an active-low input</summary>
        ActiveLow = 0,

        /// <summary>A switch on an input pin</summary>
        InputPin = 1,

        /// <summary>The Z probe stands in for the endstop</summary>
        ZProbeAsEndstop = 2,

        /// <summary>Any driver of the axis stalling</summary>
        MotorStallAny = 3,

        /// <summary>Each driver of the axis stalling individually</summary>
        MotorStallIndividual = 4
    }

    /// <summary>
    /// The object model's endstop type for an M574 S value
    /// </summary>
    /// <param name="type">The S value</param>
    /// <returns>The object model type</returns>
    private static EndstopType ToEndstopType(RrfEndstopType type) => type switch
    {
        RrfEndstopType.InputPin => EndstopType.InputPin,
        RrfEndstopType.ZProbeAsEndstop => EndstopType.ZProbeAsEndstop,
        RrfEndstopType.MotorStallAny => EndstopType.MotorStallAny,
        RrfEndstopType.MotorStallIndividual => EndstopType.MotorStallIndividual,
        _ => EndstopType.Unknown
    };

    /// <summary>
    /// How M119 reports the state of a Z probe
    /// </summary>
    /// <param name="probe">The probe, or null if none is configured</param>
    /// <returns>The state</returns>
    private static string DescribeProbeState(Probe? probe)
    {
        if (probe is null || probe.Type == ProbeType.None)
        {
            return "not stopped";
        }

        int reading = probe.Value.Count > 0 ? probe.Value[0] : 0;
        return reading >= probe.Threshold ? "at min stop" : "not stopped";
    }

    /// <summary>
    /// Describe an endstop the way M574 reports it
    /// </summary>
    /// <param name="endstop">The endstop</param>
    /// <returns>The description</returns>
    private static string DescribeEndstop(Endstop endstop) => endstop.Type switch
    {
        EndstopType.InputPin => RemoteEndstops.PortsOf(endstop) is { Length: > 1 } ports
                                ? $"switches connected to pins {string.Join(' ', ports)}"
                                : $"switch connected to pin {endstop.Port ?? "(none)"}",
        EndstopType.ZProbeAsEndstop => "Z probe",
        EndstopType.MotorStallAny => "motor stall (any driver)",
        EndstopType.MotorStallIndividual => "motor stall (individual drivers)",
        _ => "unknown"
    };

    /// <summary>
    /// Find an axis' endstop, adding it if the axis has none yet
    /// </summary>
    /// <param name="axis">Axis number</param>
    /// <returns>The endstop</returns>
    /// <remarks>The caller must hold the object model write lock</remarks>
    private Endstop GetOrCreateEndstop(int axis)
    {
        while (model.Sensors.Endstops.Count <= axis)
        {
            model.Sensors.Endstops.Add(null);
        }
        return model.Sensors.Endstops[axis] ??= new Endstop();
    }

    /// <summary>
    /// Check the P parameter of M574 against the axis it is being given to
    /// </summary>
    /// <param name="port">Port names, '+'-separated for an axis with a switch per driver</param>
    /// <param name="axis">The axis</param>
    /// <returns>The reason the ports were refused, or null if they are usable</returns>
    private static string? ValidateEndstopPorts(string port, Axis axis)
    {
        string[] ports = port.Split(RemoteEndstops.PortSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ports.Length == 0)
        {
            return null;                        // clearing the port is how an endstop is given up
        }

        if (ports.Length > Motion.Native.MotionLimits.MaxDriversPerAxis)
        {
            return $"Axis {axis.Letter} may have at most {Motion.Native.MotionLimits.MaxDriversPerAxis} endstop switches";
        }

        foreach (string switchPort in ports)
        {
            // The switches of an axis need not share a board: a move carries the address of each one
            // separately, as RepRapFirmware's SwitchEndstop keeps a board number per port
            if (!RemoteEndstops.TrySplitPort(switchPort, "Endstop port", out _, out _, out string? error))
            {
                return error;
            }
        }
        return null;
    }

    /// <summary>
    /// Ask the board carrying an endstop's port to watch it and report changes
    /// </summary>
    /// <param name="axis">Axis the endstop belongs to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the board had to say, which is an empty message if there was nothing to ask for</returns>
    /// <remarks>
    /// Until this is done the input is not reported at all, so an endstop that is configured but not
    /// monitored would silently never trigger
    /// </remarks>
    private async ValueTask<Message> CreateEndstopMonitorAsync(int axis, CancellationToken cancellationToken)
    {
        List<Message> replies = [];
        string[] ports;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            Endstop? endstop = axis < model.Sensors.Endstops.Count ? model.Sensors.Endstops[axis] : null;
            if (endstop is null || endstop.Type != EndstopType.InputPin)
            {
                return new Message();           // nothing to monitor: not a switch on a pin
            }
            ports = RemoteEndstops.PortsOf(endstop);
        }

        if (ports.Length == 0)
        {
            return new Message();               // no port named yet, so there is nothing to ask for
        }

        // An axis with a switch per driver needs one monitor per switch, each under the handle that
        // driver's moves will name
        for (int switchIndex = 0; switchIndex < ports.Length; switchIndex++)
        {
            if (!RemoteEndstops.TrySplitPort(ports[switchIndex], "Endstop port", out byte board,
                                             out string localPort, out string? error))
            {
                return new Message(MessageType.Error, error);
            }

            CanMessageCreateInputMonitorV1 message = new()
            {
                Handle = RemoteEndstops.HandleFor(axis, switchIndex),
                Threshold = 0,
                MinInterval = EndstopMinReportInterval
            };
            CanText.SetString(message.PinName, localPort);

            CanResponse response = await linkInterface.SendCanMessageAsync(board, in message, CanMessageType.StandardReply,
                                                                          cancellationToken: cancellationToken);
            Message reply = response.ToMessage();
            if (reply.Type == MessageType.Error)
            {
                return reply;                   // the switch is not being watched, so stop here
            }

            // The board answers with the switch's current state, and this is the only chance to learn
            // it: from here it reports only changes, so a switch already closed - a machine powered
            // up resting on its endstop - would read as open until somebody moved the axis by hand.
            // A homing move would then not hold the axis, because nothing knew it needed holding
            await expansionBoardManager.NoteMonitorCreatedAsync(message.Handle, response.Extra != 0,
                                                                cancellationToken);

            // The board took the port but may still have had something to say about it, which the
            // code carries back rather than dropping for not being an error
            replies.Add(reply);
        }
        return replies.ToMessage();
    }

    /// <summary>
    /// Flush the code pipeline and then wait for the machine to come to a stop
    /// </summary>
    /// <param name="code">The code being executed</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the machine is at a standstill</returns>
    private async ValueTask<bool> FlushAndWaitForStandstillAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
        {
            return false;
        }
        return await planner.WaitForStandstillAsync(cancellationToken);
    }

    /// <summary>
    /// Whether the code names a drive to configure rather than only asking for a report
    /// </summary>
    /// <param name="code">The code</param>
    /// <returns>True if it names an axis or the extruders</returns>
    /// <remarks>
    /// Waiting for standstill is only warranted when something is actually being changed. Doing it
    /// unconditionally would make a bare M92 or M906 - which DWC polls for - stall until the machine
    /// stopped, in the middle of a print
    /// </remarks>
    private static bool SetsAnyDrive(Commands.Code code)
        => code.Parameters.Any(parameter => parameter.Letter == 'E' || Axis.Letters.Contains(parameter.Letter));

    /// <summary>
    /// Convert a steps per mm value quoted at one microstepping to the microstepping in use
    /// </summary>
    /// <param name="value">The value as given</param>
    /// <param name="quotedAt">Microstepping it was quoted at, or zero if it was not</param>
    /// <param name="inUse">Microstepping the drive is set to</param>
    /// <returns>Steps per mm at the microstepping in use, never below the minimum</returns>
    private static float ScaleForMicrostepping(float value, uint quotedAt, int inUse)
    {
        if (quotedAt != 0 && inUse > 0 && quotedAt != inUse)
        {
            value = value * inUse / quotedAt;
        }
        return MathF.Max(value, MinStepsPerMm);
    }

    /// <summary>
    /// Read the E parameter as one value per extruder
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="move">The move subsystem</param>
    /// <param name="values">One value per configured extruder</param>
    /// <returns>True if the code carried an E parameter</returns>
    /// <remarks>
    /// A single value applies to every extruder, which is how nearly every configuration is written.
    /// More than one is taken positionally, and any extruder the list does not reach keeps its setting
    /// </remarks>
    private static bool TryGetExtruderValues(Commands.Code code, Move move, out float[] values)
    {
        if (!code.TryGetFloatArray('E', out float[]? given) || given.Length == 0)
        {
            values = [];
            return false;
        }

        values = new float[given.Length == 1 ? move.Extruders.Count : Math.Min(given.Length, move.Extruders.Count)];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = given.Length == 1 ? given[0] : given[i];
        }
        return true;
    }

    /// <summary>
    /// Apply a jerk value to the machine limit, the printing limit, or both
    /// </summary>
    /// <param name="value">The jerk in mm/min</param>
    /// <param name="setMax">Whether this sets the machine limit as well as the printing one</param>
    /// <param name="getMax">Reads the machine limit</param>
    /// <param name="setMaxValue">Writes the machine limit</param>
    /// <param name="setPrintingValue">Writes the printing limit</param>
    /// <remarks>
    /// The printing jerk is held at or below the machine limit, so setting only the printing jerk
    /// above the limit clamps it rather than raising the limit
    /// </remarks>
    private static void SetJerk(float value, bool setMax, Func<float> getMax, Action<float> setMaxValue, Action<float> setPrintingValue)
    {
        if (setMax)
        {
            setMaxValue(value);
            setPrintingValue(value);
        }
        else
        {
            setPrintingValue(MathF.Min(value, getMax()));
        }
    }

    /// <summary>
    /// The first motion system, adding one if the machine has none yet
    /// </summary>
    /// <param name="move">The move subsystem</param>
    /// <returns>The motion system</returns>
    /// <remarks>The caller must hold the object model write lock</remarks>
    private static MotionSystem GetOrCreateMotionSystem(Move move)
    {
        if (move.MotionSystems.Count == 0)
        {
            move.MotionSystems.Add(new MotionSystem());
        }
        return move.MotionSystems[0];
    }

    /// <summary>
    /// Create an axis that has just been named for the first time
    /// </summary>
    /// <param name="letter">Its axis letter</param>
    /// <param name="wrapType">Wrap type from the R parameter, or null if it was not given</param>
    /// <param name="rotational">Whether it is rotational per the S parameter, or null if it was not given</param>
    /// <returns>The new axis</returns>
    private static Axis CreateAxis(char letter, int? wrapType, bool? rotational)
    {
        // A through D default to rotating, because that is what they conventionally are; every other
        // letter defaults to translating
        bool continuous = wrapType.HasValue ? wrapType.Value == 1 : letter is >= 'A' and <= 'D';
        return new Axis
        {
            Letter = letter,
            Visible = true,
            ContinuousRotation = continuous,
            Rotational = rotational ?? continuous,
            MachinePosition = 0.0f,
            UserPosition = 0.0f
        };
    }

    /// <summary>
    /// Whether a driver can be addressed, recording why not if it cannot
    /// </summary>
    /// <param name="driver">The driver</param>
    /// <param name="warnings">Where to record the reason it was rejected</param>
    /// <returns>True if the driver is usable</returns>
    /// <remarks>
    /// A board that has not announced itself yet is not a reason to reject a driver: config.g runs
    /// before the expansion boards have necessarily all been seen
    /// </remarks>
    private bool IsValidDriver(DriverId driver, List<string> warnings)
    {
        if (CanAddresses.HasNoHardware(driver.Board))
        {
            warnings.Add(CanAddresses.NoHardwareMessage($"Driver {driver}"));
            return false;
        }

        // MaxMotors is zero for a board that has announced itself but not yet reported its details,
        // which says nothing about whether the driver exists
        Board? board = model.Boards.FirstOrDefault(b => b.CanAddress == driver.Board);
        if (board is not null && board.MaxMotors > 0 && driver.Port >= board.MaxMotors)
        {
            warnings.Add($"Driver {driver} does not exist");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Read the drivers M584 assigns to one drive letter
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="letter">The axis letter, or E for the extruders</param>
    /// <param name="drivers">The drivers named for it, empty if the letter was given without any</param>
    /// <returns>True if the code names the letter at all</returns>
    /// <remarks>
    /// A letter given without a value means no drivers, which is how a drive gives up the ones it
    /// has: an axis stays in place but can no longer move, and a bare E leaves no extruders at all
    /// </remarks>
    private static bool TryGetDrivers(Commands.Code code, char letter, out DriverId[] drivers)
    {
        DuetAPI.Commands.CodeParameter? parameter = code.GetParameter(letter);
        if (parameter is not null && parameter.IsNull)
        {
            drivers = [];
            return true;
        }

        if (code.TryGetDriverIdArray(letter, out DriverId[]? given))
        {
            drivers = given;
            return true;
        }

        drivers = [];
        return false;
    }

    /// <summary>
    /// Find a driver that the mapping M584 asks for would give to two drives at once
    /// </summary>
    /// <param name="move">The move model as it stands</param>
    /// <param name="axisMapping">The drivers the code assigns to each axis letter it names</param>
    /// <param name="extruderMapping">The drivers it assigns to the extruders, or null if it names none</param>
    /// <returns>Why the mapping cannot be applied, or null if every driver is claimed once</returns>
    /// <remarks>
    /// A drive the code says nothing about keeps the drivers it has, so those are taken. One it does
    /// name gives up its old drivers, which is what lets a single code swap the drivers of two axes
    /// </remarks>
    private static string? FindDriverConflict(Move move, List<(char Letter, DriverId[] Drivers)> axisMapping, DriverId?[]? extruderMapping)
    {
        Dictionary<DriverId, string> owners = [];

        foreach (Axis axis in move.Axes)
        {
            if (!axisMapping.Any(mapping => mapping.Letter == axis.Letter))
            {
                foreach (DriverId driver in axis.Drivers)
                {
                    owners[driver] = $"axis {axis.Letter}";
                }
            }
        }

        // The E parameter is the whole set of extruders, so naming it frees every extruder driver
        if (extruderMapping is null)
        {
            for (int i = 0; i < move.Extruders.Count; i++)
            {
                if (move.Extruders[i].Driver is DriverId driver)
                {
                    owners[driver] = $"extruder {i}";
                }
            }
        }

        foreach ((char letter, DriverId[] drivers) in axisMapping)
        {
            foreach (DriverId driver in drivers)
            {
                if (owners.TryGetValue(driver, out string? owner))
                {
                    return $"Driver {driver} is already used by {owner}";
                }
                owners[driver] = $"axis {letter}";
            }
        }

        if (extruderMapping is not null)
        {
            for (int i = 0; i < extruderMapping.Length; i++)
            {
                if (extruderMapping[i] is DriverId driver)
                {
                    if (owners.TryGetValue(driver, out string? owner))
                    {
                        return $"Driver {driver} is already used by {owner}";
                    }
                    owners[driver] = $"extruder {i}";
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Apply one of the three current settings to an axis
    /// </summary>
    /// <param name="axis">The axis</param>
    /// <param name="which">906, 913 or 917</param>
    /// <param name="value">The value as given</param>
    private static void SetCurrent(Axis axis, int which, float value)
    {
        switch (which)
        {
            case 913: axis.PercentCurrent = ClampPercent(value); break;
            case 917: axis.PercentStstCurrent = ClampPercent(value); break;
            default: axis.Current = (int)MathF.Round(MathF.Max(value, 0.0f)); break;
        }
    }

    /// <summary>
    /// Apply one of the three current settings to an extruder
    /// </summary>
    /// <param name="extruder">The extruder</param>
    /// <param name="which">906, 913 or 917</param>
    /// <param name="value">The value as given</param>
    private static void SetCurrent(Extruder extruder, int which, float value)
    {
        switch (which)
        {
            case 913: extruder.PercentCurrent = ClampPercent(value); break;
            case 917: extruder.PercentStstCurrent = ClampPercent(value); break;
            default: extruder.Current = (int)MathF.Round(MathF.Max(value, 0.0f)); break;
        }
    }

    /// <summary>Read back whichever current setting a code addresses on an axis</summary>
    /// <param name="axis">The axis</param>
    /// <param name="which">906, 913 or 917</param>
    /// <returns>The value</returns>
    private static int CurrentOf(Axis axis, int which) => which switch
    {
        913 => axis.PercentCurrent,
        917 => axis.PercentStstCurrent ?? 0,
        _ => axis.Current
    };

    /// <summary>Read back whichever current setting a code addresses on an extruder</summary>
    /// <param name="extruder">The extruder</param>
    /// <param name="which">906, 913 or 917</param>
    /// <returns>The value</returns>
    private static int CurrentOf(Extruder extruder, int which) => which switch
    {
        913 => extruder.PercentCurrent,
        917 => extruder.PercentStstCurrent ?? 0,
        _ => extruder.Current
    };

    /// <summary>
    /// The value to send to a driver after a current setting changed
    /// </summary>
    /// <param name="axis">The axis</param>
    /// <param name="which">906, 913 or 917</param>
    /// <returns>Standstill percentage for M917, otherwise the current in mA</returns>
    /// <remarks>
    /// M913 is a percentage of the configured current rather than a setting of its own on the driver,
    /// so what goes out for both M906 and M913 is the resulting current
    /// </remarks>
    private static float CurrentToSend(Axis axis, int which)
        => which == 917 ? axis.PercentStstCurrent ?? 0 : axis.Current * axis.PercentCurrent / 100.0f;

    /// <summary>
    /// The value to send to an extruder's driver after a current setting changed
    /// </summary>
    /// <param name="extruder">The extruder</param>
    /// <param name="which">906, 913 or 917</param>
    /// <returns>Standstill percentage for M917, otherwise the current in mA</returns>
    private static float CurrentToSend(Extruder extruder, int which)
        => which == 917 ? extruder.PercentStstCurrent ?? 0 : extruder.Current * extruder.PercentCurrent / 100.0f;

    /// <summary>Clamp a percentage to the range a driver accepts</summary>
    /// <param name="value">The value as given</param>
    /// <returns>The clamped percentage</returns>
    private static int ClampPercent(float value) => Math.Clamp((int)MathF.Round(value), 0, 100);

    /// <summary>
    /// Note that every driver of an axis needs to be put into a given state
    /// </summary>
    /// <param name="toUpdate">List being built</param>
    /// <param name="drivers">The axis' drivers</param>
    /// <param name="mode">Driver state to apply</param>
    /// <param name="idlePercent">Idle current percentage</param>
    private static void ApplyDriverState(List<RemoteDrivers.DriverValue<(ushort, ushort)>> toUpdate, IEnumerable<DriverId> drivers,
                                         ushort mode, ushort idlePercent)
    {
        foreach (DriverId driver in drivers)
        {
            AddDriverState(toUpdate, driver, mode, idlePercent);
        }
    }

    /// <summary>
    /// Note that one driver needs to be put into a given state
    /// </summary>
    /// <param name="toUpdate">List being built</param>
    /// <param name="driver">The driver, or null if the drive has none assigned</param>
    /// <param name="mode">Driver state to apply</param>
    /// <param name="idlePercent">Idle current percentage</param>
    private static void AddDriverState(List<RemoteDrivers.DriverValue<(ushort, ushort)>> toUpdate, DriverId? driver,
                                       ushort mode, ushort idlePercent)
    {
        if (driver is not null)
        {
            toUpdate.Add(new RemoteDrivers.DriverValue<(ushort, ushort)>(driver, (mode, idlePercent)));
        }
    }

    /// <summary>
    /// Note that every driver of an axis needs its steps per mm and microstepping sent to it
    /// </summary>
    /// <param name="toUpdate">List being built</param>
    /// <param name="drivers">The axis' drivers</param>
    /// <param name="stepsPerMm">Steps per mm of the axis</param>
    /// <param name="microstepping">Microstepping of the axis</param>
    private static void AddDrivers(List<RemoteDrivers.DriverValue<(float, int, bool)>> toUpdate, IEnumerable<DriverId> drivers,
                                   float stepsPerMm, Microstepping microstepping)
    {
        foreach (DriverId driver in drivers)
        {
            AddDriver(toUpdate, driver, stepsPerMm, microstepping);
        }
    }

    /// <summary>
    /// Note that one driver needs its steps per mm and microstepping sent to it
    /// </summary>
    /// <param name="toUpdate">List being built</param>
    /// <param name="driver">The driver, or null if the drive has none assigned</param>
    /// <param name="stepsPerMm">Steps per mm of the drive</param>
    /// <param name="microstepping">Microstepping of the drive</param>
    private static void AddDriver(List<RemoteDrivers.DriverValue<(float, int, bool)>> toUpdate, DriverId? driver,
                                  float stepsPerMm, Microstepping microstepping)
    {
        if (driver is not null)
        {
            toUpdate.Add(new RemoteDrivers.DriverValue<(float, int, bool)>(
                driver, (stepsPerMm, microstepping.Value, microstepping.Interpolated)));
        }
    }

    /// <summary>
    /// Send the steps per mm and microstepping of the given drivers to the boards that carry them
    /// </summary>
    /// <param name="toUpdate">Drivers and their settings</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, carrying anything the boards objected to</returns>
    private async ValueTask<Message> UpdateRemoteDriversAsync(List<RemoteDrivers.DriverValue<(float, int, bool)>> toUpdate,
                                                              CancellationToken cancellationToken)
    {
        if (toUpdate.Count == 0)
        {
            return new Message();
        }

        IList<Message> replies = await RemoteDrivers.SetStepsPerMmAndMicrosteppingAsync(linkInterface, toUpdate, cancellationToken);
        return replies.ToMessage();
    }

    /// <summary>
    /// Send the motor currents of the given drivers to the boards that carry them
    /// </summary>
    /// <param name="toUpdate">Drivers and their currents in mA</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, carrying anything the boards objected to</returns>
    private async ValueTask<Message> UpdateRemoteDriversAsync(List<RemoteDrivers.DriverValue<float>> toUpdate,
                                                              CancellationToken cancellationToken)
    {
        if (toUpdate.Count == 0)
        {
            return new Message();
        }

        IList<Message> replies = await RemoteDrivers.SetMotorCurrentsAsync(linkInterface, toUpdate, cancellationToken);
        return replies.ToMessage();
    }

    /// <summary>
    /// Describe a microstepping setting the way RepRapFirmware reports it
    /// </summary>
    /// <param name="microstepping">The setting</param>
    /// <returns>The description</returns>
    private static string Describe(Microstepping microstepping)
        => microstepping.Interpolated ? $"{microstepping.Value}(on)" : microstepping.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Build the report these codes produce when given no values to set
    /// </summary>
    /// <param name="move">The move subsystem</param>
    /// <param name="prefix">Text the report opens with</param>
    /// <param name="describeAxis">Renders one axis' value</param>
    /// <param name="describeExtruder">Renders one extruder's value</param>
    /// <param name="axisSeparator">What comes between an axis letter and its value</param>
    /// <param name="extruderHeader">What introduces the extruder values</param>
    /// <param name="firstExtruderSeparator">What comes between the header and the first extruder value</param>
    /// <returns>The report</returns>
    /// <remarks>
    /// The shape is RepRapFirmware's and is kept exactly, down to where the colons and spaces fall:
    /// M92, M201, M203 and M566 report "... E: 420.000:420.000" while M350 and M906 report
    /// "... E:16(on):16(on)". Existing macros and user interfaces parse these strings
    /// </remarks>
    private static string ReportPerDrive(Move move, string prefix, Func<Axis, string> describeAxis, Func<Extruder, string> describeExtruder,
                                         string axisSeparator = ": ", string extruderHeader = "E:", string firstExtruderSeparator = " ")
    {
        StringBuilder builder = new(prefix);
        foreach (Axis axis in move.Axes)
        {
            builder.Append(axis.Letter).Append(axisSeparator).Append(describeAxis(axis)).Append(", ");
        }

        builder.Append(extruderHeader);
        string separator = firstExtruderSeparator;
        foreach (Extruder extruder in move.Extruders)
        {
            builder.Append(separator).Append(describeExtruder(extruder));
            separator = ":";
        }
        return builder.ToString();
    }

    /// <summary>
    /// Report which drivers each axis and extruder is mapped to (M584 with no parameters)
    /// </summary>
    /// <param name="move">The move subsystem</param>
    /// <returns>The report</returns>
    private static string ReportDriveMapping(Move move)
    {
        StringBuilder builder = new("Driver assignments:");
        foreach (Axis axis in move.Axes)
        {
            builder.Append(' ').Append(axis.Letter)
                   .Append(string.Join(':', axis.Drivers.Select(driver => driver.ToString())));
        }

        builder.Append(" E");
        builder.Append(string.Join(':', move.Extruders.Select(extruder => extruder.Driver?.ToString() ?? "none")));

        int visible = move.Axes.Count(axis => axis.Visible);
        builder.Append(", ").Append(visible).Append(" axes visible");
        return builder.ToString();
    }

    #endregion
}
