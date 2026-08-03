using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Link.Protocol.CanMessages;
using System;
using System.Collections.Generic;
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
/// Everything here writes the object model and nothing else: <c>move.axes[]</c>,
/// <c>move.extruders[]</c> and <c>move.motionSystems[]</c> are the configuration, and
/// <see cref="Motion.MotionParameters"/> is rebuilt from them by
/// <see cref="Motion.MovePlanner.ReconfigureAsync"/>. There is deliberately no second copy of a
/// setting anywhere in this file.
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

    /// <summary>
    /// M92: set or report the steps per mm of each drive
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message?> HandleStepsPerMmAsync(Commands.Code code, CancellationToken cancellationToken)
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

        await planner.ReconfigureAsync(cancellationToken);
        return await UpdateRemoteDriversAsync(toUpdate, cancellationToken);
    }

    /// <summary>
    /// M201: set or report the acceleration of each drive. M201.1 does the same for the reduced
    /// accelerations used by probing and stall homing moves
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message?> HandleAccelerationsAsync(Commands.Code code, CancellationToken cancellationToken)
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

        await planner.ReconfigureAsync(cancellationToken);
        return new Message();
    }

    /// <summary>
    /// M203: set or report the maximum speed of each drive and the slowest a move may run
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message?> HandleMaxFeedratesAsync(Commands.Code code, CancellationToken cancellationToken)
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

        await planner.ReconfigureAsync(cancellationToken);
        return new Message();
    }

    /// <summary>
    /// M204: set or report the acceleration limits that apply to a move as a whole
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message?> HandleMoveAccelerationsAsync(Commands.Code code, CancellationToken cancellationToken)
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

        await planner.ReconfigureAsync(cancellationToken);
        return new Message();
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
    private async ValueTask<Message?> HandleJerkAsync(Commands.Code code, CancellationToken cancellationToken)
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

        if (seenAxis)
        {
            await planner.ReconfigureAsync(cancellationToken);
        }
        return new Message();
    }

    /// <summary>
    /// M208: set or report how far each axis may travel
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message?> HandleAxisLimitsAsync(Commands.Code code, CancellationToken cancellationToken)
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
    private async ValueTask<Message?> HandleMicrosteppingAsync(Commands.Code code, CancellationToken cancellationToken)
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

        await planner.ReconfigureAsync(cancellationToken);
        return await UpdateRemoteDriversAsync(toUpdate, cancellationToken);
    }

    /// <summary>
    /// M400: wait for the moves already commanded to finish
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message?> HandleWaitForMovesAsync(Commands.Code code, CancellationToken cancellationToken)
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
    /// until that has happened, which is why config.g runs M584 before the rest of the motion setup
    /// </remarks>
    private async ValueTask<Message?> HandleDriveMappingAsync(Commands.Code code, CancellationToken cancellationToken)
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

            foreach (char letter in Axis.Letters)
            {
                if (!code.TryGetDriverIdArray(letter, out DriverId[]? drivers))
                {
                    continue;
                }
                seen = true;

                DriverId[] valid = [.. drivers.Where(driver => IsValidDriver(driver, warnings))];

                Axis? axis = move.Axes.FirstOrDefault(a => a.Letter == letter);
                if (axis is null)
                {
                    axis = CreateAxis(letter, seenWrapType ? wrapType : null, seenRotational ? rotational == 1 : null);
                    move.Axes.Add(axis);
                }

                axis.Drivers.Clear();
                foreach (DriverId driver in valid)
                {
                    axis.Drivers.Add(driver);
                }
                AddDrivers(toUpdate, axis.Drivers, axis.StepsPerMm, axis.Microstepping);
            }

            if (code.TryGetDriverIdArray('E', out DriverId[]? extruderDrivers))
            {
                seen = true;

                // The E list is the whole set of extruders, so one that is no longer named goes away
                while (move.Extruders.Count > extruderDrivers.Length)
                {
                    move.Extruders.RemoveAt(move.Extruders.Count - 1);
                }
                while (move.Extruders.Count < extruderDrivers.Length)
                {
                    move.Extruders.Add(new Extruder());
                }

                for (int i = 0; i < extruderDrivers.Length; i++)
                {
                    Extruder extruder = move.Extruders[i];
                    extruder.Driver = IsValidDriver(extruderDrivers[i], warnings) ? extruderDrivers[i] : null;
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

        await planner.ReconfigureAsync(cancellationToken);

        Message result = await UpdateRemoteDriversAsync(toUpdate, cancellationToken);
        foreach (string warning in warnings)
        {
            result.Append(MessageType.Warning, warning);
        }
        return result;
    }

    /// <summary>
    /// M906: set or report the motor current of each drive, and how it is reduced when idle
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message?> HandleMotorCurrentsAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        List<RemoteDrivers.DriverValue<float>> toUpdate = [];
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
                    axis.Current = (int)MathF.Round(MathF.Max(value, 0.0f));
                    foreach (DriverId driver in axis.Drivers)
                    {
                        toUpdate.Add(new RemoteDrivers.DriverValue<float>(driver, axis.Current));
                    }
                    seen = true;
                }
            }

            if (TryGetExtruderValues(code, move, out float[]? extruderValues))
            {
                for (int i = 0; i < extruderValues.Length; i++)
                {
                    Extruder extruder = move.Extruders[i];
                    extruder.Current = (int)MathF.Round(MathF.Max(extruderValues[i], 0.0f));
                    if (extruder.Driver is not null)
                    {
                        toUpdate.Add(new RemoteDrivers.DriverValue<float>(extruder.Driver, extruder.Current));
                    }
                }
                seen = true;
            }

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

            if (!seen)
            {
                report = ReportPerDrive(move, "Motor current (mA) - ",
                                        axis => axis.Current.ToString(CultureInfo.InvariantCulture),
                                        extruder => extruder.Current.ToString(CultureInfo.InvariantCulture),
                                        axisSeparator: ":", extruderHeader: "E", firstExtruderSeparator: ":")
                         + string.Format(CultureInfo.InvariantCulture, ", idle factor {0}%, timeout {1:F1} sec",
                                         (int)(move.Idle.Factor * 100.0f), move.Idle.Timeout);
            }
        }

        if (!seen)
        {
            return new Message(MessageType.Success, report!);
        }
        return await UpdateRemoteDriversAsync(toUpdate, cancellationToken);
    }

    #region Helpers

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

        IList<string> replies = await RemoteDrivers.SetStepsPerMmAndMicrosteppingAsync(linkInterface, toUpdate, cancellationToken);
        return replies.Count > 0 ? new Message(MessageType.Warning, string.Join('\n', replies)) : new Message();
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

        IList<string> replies = await RemoteDrivers.SetMotorCurrentsAsync(linkInterface, toUpdate, cancellationToken);
        return replies.Count > 0 ? new Message(MessageType.Warning, string.Join('\n', replies)) : new Message();
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
