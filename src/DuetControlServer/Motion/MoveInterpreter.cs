using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion.Kinematics;
using DuetControlServer.Motion.Native;
using static DuetControlServer.Motion.AxisIndices;

namespace DuetControlServer.Motion;

/// <summary>
/// Turns a movement code into the move the engine is asked to run
/// </summary>
/// <remarks>
/// <para>
/// RepRapFirmware's <c>GCodes::DoStraightMove</c> and the helpers it leans on. Everything here is
/// steps 1 to 6 of building a move - reading the code's words, moving the interpreter's own position
/// on, applying the transforms, limiting the target and deciding how many pieces the move needs -
/// and none of it talks to the machine. What the move is submitted to, retried against and waited
/// for is the G-code handler's business.
/// </para>
/// <para>
/// Everything it reads and writes lives in the object model - the axis positions in
/// <c>move.axes[]</c>, the extruder positions in <c>move.extruders[]</c> - so the state a move is
/// planned against is the state every API reports. The caller must hold the object model write lock
/// and the planner lock: building a move is a delta from the interpreter position held here, and it
/// advances that position, so two channels building at once would each measure from the wrong place
/// </para>
/// </remarks>
/// <param name="model">Object model</param>
/// <param name="builder">
/// Where the last queued move left the machine, and the configuration it was planned against
/// </param>
/// <param name="state">The interpreter position a move is measured from and written back to</param>
/// <param name="bedCompensation">Height map correction</param>
/// <param name="endstopCorrection">Undoes the overshoot of a move an endstop cut short</param>
/// <param name="currentTool">
/// The selected tool, whose offsets and axis mapping the transform needs. A function rather than the
/// tool itself, because which tool is selected changes between one move and the next
/// </param>
/// <param name="closedEndstopSwitches">
/// Which switches of an endstop are closed, switch by switch, as the arming needs them
/// </param>
internal sealed class MoveInterpreter(
    Model.ObjectModel model,
    MoveBuilder builder,
    MovementState state,
    BedCompensation bedCompensation,
    EndstopCorrection endstopCorrection,
    Func<Tool?> currentTool,
    Func<int, uint> closedEndstopSwitches)
{
    /// <summary>
    /// Millimetres per inch, for G20
    /// </summary>
    private const float MmPerInch = 25.4f;

    /// <summary>
    /// G-code feed rates are per minute; everything below the interpreter is per second
    /// </summary>
    private const float SecondsPerMinute = 60.0f;

    /// <summary>
    /// How fast a G0 goes when it is a rapid rather than a travel move
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>MaximumG0FeedRate</c> in mm/min. The move is still limited by the axis
    /// speeds the machine was configured with, so this is an upper bound rather than a promise
    /// </remarks>
    private const float MaximumG0FeedRate = 60000.0f;

    /// <summary>
    /// Longest a single segment may take, seconds
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>MaxSegmentTime</c>. The step clock is 32 bits at 750kHz, so it wraps in
    /// under an hour; a move that occupies a large part of that cannot be timed against it
    /// </remarks>
    private const float MaxSegmentSeconds = 5.0f * 60.0f;

    /// <summary>
    /// The machine being planned for, as last read from the object model
    /// </summary>
    private MotionParameters Parameters => builder.Parameters;

    /// <summary>
    /// Read a movement code's parameters into a move
    /// </summary>
    /// <param name="code">The code, as parsed</param>
    /// <param name="input">The channel's interpreter state</param>
    /// <param name="isCoordinated">Whether this is a G1</param>
    /// <param name="moveType">What kind of move the H parameter asked for</param>
    /// <returns>The move</returns>
    /// <exception cref="GCodeException">The move cannot be built</exception>
    /// <remarks>The caller must hold the object model write lock</remarks>
    public RawMove BuildRawMove(DuetAPI.Commands.Code code, InputChannel input, bool isCoordinated, MoveType moveType,
                                IReadOnlyList<EndstopPlan> endstopPlans)
    {
        MotionParameters parameters = Parameters;
        int numAxes = parameters.SharedAxisCount(model.Move);
        float unitScale = input.DistanceUnit == DistanceUnit.Inch ? MmPerInch : 1.0f;
        Tool? tool = currentTool();

        RawMove raw = new()
        {
            IsCoordinated = isCoordinated,
            InverseTimeMode = input.InverseTimeMode,
            XAxes = ToolTransform.AxisBitmap(tool, model.Move, 'X'),
            YAxes = ToolTransform.AxisBitmap(tool, model.Move, 'Y'),

            // H selected what kind of move this is. H1, H3 and H4 stop on the endstops - that is
            // homing, measuring an axis' length, and probing - and H2 is an individual motor move
            // that ignores them
            MoveType = moveType,
            CheckEndstops = moveType.ChecksEndstops()
        };

        // How much of this code is still to do. It is one for everything except the first job-file
        // move after a resume that stopped part-way through a code - see MoveFractionToSkip
        float moveFraction = 1.0f - MoveFractionToSkipFor(code);

        // G53 asks for machine coordinates on this line only, so neither the workplace offset nor
        // (once tools exist) the tool offset applies to it
        bool machineCoordinates = code.Flags.HasFlag(CodeFlags.EnforceAbsolutePosition);
        bool runningSystemMacro = code.Flags.HasFlag(CodeFlags.IsFromSystemMacro);
        uint axesMentioned = AxesMentioned(code, numAxes);

        if (moveType == MoveType.Normal)
        {
            // Where the move starts from, taken before the axis words below move the interpreter on.
            // It is the same forward transform the target goes through, which is what keeps the two
            // ends of the line in one coordinate space however many terms that transform grows.
            // RepRapFirmware captures ms.initialCoords at the same instant and for the same reason.
            //
            // TODO RepRapFirmware copies its initialCoords from ms.raw.coords, which persists from
            // the previous move, where this evaluates the transform afresh. The two differ whenever a
            // term of the transform changed since - and M290 during a print is exactly that, so this
            // is reachable now rather than theoretical. RRF interpolates from the old babystep to the
            // new one across the move; here the whole change lands on the first segment. Closing it
            // means carrying the previous move's coords rather than re-deriving them
            ToolTransform.Apply(tool, model.Move, state, raw.InitialCoords, numAxes);

            // The axes start where the last move left them, which is what an axis the code does not
            // mention is being commanded to. RepRapFirmware gets this for free - ms.raw.coords is a
            // member of a long-lived MovementState and simply carries over, which is also how an
            // extruder-only move leaves the axes alone. A RawMove here is constructed per move, so
            // "not written" would mean zero rather than unchanged, and zero is a dive to the origin
            raw.InitialCoords.AsSpan(0, numAxes).CopyTo(raw.Coords);

            for (int axis = 0; axis < numAxes; axis++)
            {
                Axis axisConfig = model.Move.Axes[axis];
                if (!code.TryGetFloat(axisConfig.Letter, out float value))
                {
                    continue;
                }

                float moveArg = axisConfig.Rotational ? value : value * unitScale;

                // The interpreter's own position is what a move is measured from and written back
                // to. It runs ahead of the machine by however many moves are still queued, which is
                // exactly why it cannot be read back out of the object model's reported positions
                if (input.AxesRelative)
                {
                    // A relative word says how far to go rather than where to end up, so a move
                    // that is already part done has only the rest of it left to ask for
                    state.CurrentUserPosition[axis] += moveArg * moveFraction;
                }
                else if (machineCoordinates)
                {
                    // g53 ignores tool offsets as well as workplace coordinates
                    // TODO add the current tool offset / axisScaleFactor
                    state.CurrentUserPosition[axis] = moveArg;
                }
                else if (runningSystemMacro)
                {
                    // don't apply workplace offsets to commands in system macros
                    state.CurrentUserPosition[axis] = moveArg;
                }
                else
                {
                    state.CurrentUserPosition[axis] = moveArg + WorkplaceOffset(axisConfig, WorkplaceNumber);
                }

                if (axisConfig.Rotational)
                {
                    raw.RotationalAxesMentioned = true;
                }
                else
                {
                    raw.LinearAxesMentioned = true;
                }
            }

            // An axis the machine does not know the position of cannot be moved to a coordinate,
            // because there is no coordinate system to move it in. M564 decides whether that is
            // refused outright, and the geometry widens the set where its axes are coupled
            // This might throw a GCodeException
            // TODO use tool axis mapping to get the actual axes to move
            CheckEnoughAxesHomed(axesMentioned, numAxes); // TODO if doingManualBedProbe then skip this check
        }
        else
        {
            // A special move bypasses the user coordinate system entirely: no workplace offset, no
            // babystepping and no bed compensation, and the interpreter's position is left alone
            // because a motor position is not an axis position. RepRapFirmware does the same, which
            // is why it never writes currentUserPosition on this path
            SeedSpecialMoveCoordinates(raw, numAxes);

            // A special move carries no bed compensation, so where it starts is what the seed just
            // wrote - in whichever coordinate space that was. Recorded for the same reason as above:
            // whatever measures the move has to measure it against the space it is expressed in
            raw.Coords.AsSpan(0, numAxes).CopyTo(raw.InitialCoords);

            for (int axis = 0; axis < numAxes; axis++)
            {
                Axis axisConfig = model.Move.Axes[axis];
                if (!code.TryGetFloat(axisConfig.Letter, out float value))
                {
                    continue;
                }

                if (!input.AxesRelative && parameters.Geometry is LinearDeltaKinematicsEngine)
                {
                    // A delta's motor positions are carriage heights, and where a carriage has to be
                    // for the head to reach a point depends on the other two. So there is no absolute
                    // position to give one motor, only an amount to move it by
                    throw new GCodeException("Attempt to move individual motors of a delta machine to absolute positions");
                }

                float moveArg = axisConfig.Rotational ? value : value * unitScale;
                if (input.AxesRelative)
                {
                    raw.Coords[axis] += moveArg * moveFraction;
                }
                else
                {
                    raw.Coords[axis] = moveArg;
                }

                if (axisConfig.Rotational)
                {
                    raw.RotationalAxesMentioned = true;
                }
                else
                {
                    raw.LinearAxesMentioned = true;
                }
            }
        }

        // Pausing during an endstop move is not safe: it may stop short, so where it would resume
        // from is not known until it has finished. Every other move may be stopped after, segment
        // boundaries included - the resume re-reads the code and asks only for the rest of it, which
        // is what MoveFractionToSkip is for.
        //
        // TODO an arc move must not carry this on anything but its last segment, and firmware
        // retraction must not carry it at all. RepRapFirmware clears it for both (GCodes.cpp:3213
        // and :4557): an arc re-read from part-way along recomputes its centre from the wrong start
        // - which is what its restart point's InitialUserC0/InitialUserC1 exist to prevent - and a
        // retraction re-read is a second retraction. Neither G2/G3 nor G10/G11 is implemented yet,
        // so there is nothing to clear it on today
        raw.CanPauseAfter = !raw.CheckEndstops;

        // Before the endstops, because arming a stall endstop needs the speeds this works out
        LoadFeedRate(code, input, raw); // Can throw GCodeException

        if (raw.CheckEndstops)
        {
            // TODO support extruder homing. This check should use `axesMentioned != 0 && code.HasParameter('E')` but `ApplyEndstops()` doesn't support extruders currently
            if (code.HasParameter('E'))
            {
                // The extruder speeds an extruder endstop is validated against are worked out from
                // the move's total extrusion, which an axis moving at the same time invalidates.
                // RepRapFirmware refuses the combination rather than arming both badly
                throw new GCodeException("Cannot enable both axis and extruder endstops in the same move");
            }

            ApplyEndstops(endstopPlans, raw, numAxes); // can throw GCodeException
        }

        bool hasExtrusion = ApplyExtrusion(code, input, raw, unitScale, moveFraction);
        if (hasExtrusion || axesMentioned != 0)
        {
            // TODO check if first move since skipping an object

            if (raw.HasPositiveExtrusion && axesMentioned != 0)
            {
                // TODO update the object coordinates list
            }

            if (moveType != MoveType.Normal)
            {
                // It is a raw motor move, so do it in a single semgnet and wait for it to complete
                // TODO set the total segments to 1
            }
            else if (axesMentioned == 0)
            {
                // An extruder-only move deliberately does not go through the tool transform, so the
                // axes keep the coordinates seeded above. RepRapFirmware says why: deferring means a
                // tool offset that changed since the last move does not move the axes until an axis
                // move asks them to, rather than the change coming out as motion on a pure extrusion
                // TODO set the total segments to 1
            }
            else
            {
                // TODO support coordinate rotation

                // Apply the tool offsets, babystepping, Z hop and axis scaling
                ToolTransform.Apply(tool, model.Move, state, raw.Coords, numAxes, axesMentioned);

                // TODO supoort keepout zones, keep if move enters keepout zone
                // TODO collision checker for multiple motion systems

                // Only limit the positions of axes that have been mentioned explicitly.
                // This avoids at least two problems:
                // 1. When supporting multiple motion systems, if a M208 axis limit was changed and an axis coordinate was outside that limit,
                //    but we don't own the axis, then if we move that axis there will be a problem when SaveOwnAxisCoordinates is called
                //    because the new coordinate won't be saved.
                // 2. If a linear axis is being limited, but the move is for a rotational axis that is already in the correct position,
                //    then the code in DDA::InitStandardMove will throw it away because neither linearAxesMoving nor rotationalAxesMoving will be set.
                //    This was an actual problem on my tool changer.
                // After the extrusion, because whether the move prints decides what may be done about a
                // target that cannot be reached in a straight line, and before the bed compensation,
                // because the height map is a correction to a position the machine can already reach
                // TODO Update the above comment with DSF specific details (instead of current RRF details) when the code is written.
                LimitPosition(raw, input.AxesRelative, axesMentioned, raw.HasPositiveExtrusion, numAxes); // can throw GCodeException

                // Flag whether we should use pressure advance, if there is any extrusion in this move.
                // We assume it is a normal printing move needing pressure advance if there is forward extrusion and XYU... movement (we don't count Z).
                // The movement code will only apply pressure advance if there is forward extrusion, so we only need to check for XYU... movement here.
                // TODO with multi axis machines, the Z axis exclusion may be harmful. Some print tests are likely needed to see if this is the case
                if (raw.HasPositiveExtrusion)
                {
                    raw.UsePressureAdvance = MentionsAxisOtherThanZ(code, numAxes);
                }

                // The bed compensation is deliberately not applied here. It is a correction that depends
                // on where the nozzle is, so it belongs to each segment rather than to the move
                raw.SegmentCount = SegmentCountFor(raw, numAxes);
            }

            // The fraction belongs to one move and this is it, so it goes no further. Cleared here
            // rather than when the move completes because the interpreter is what reads it, and the
            // interpreter has already moved on to the next code by then. RepRapFirmware's ClearMove
            if (moveFraction != 1.0f)
            {
                state.MoveFractionToSkip = 0.0f;
            }

            // This is where RepRapFirmware's `FinaliseMove()` ends. The rest of what it does is here
            // or has moved: `canPauseAfter` above, the segment count above that, the extrusion per
            // segment in `SegmentedMove.From()` once this returns. The one thing that is deliberately
            // elsewhere is the file position, which no move carries - the engine has no idea what a
            // file is, so `JobMoveIndex` keeps it on this side, keyed by move id
        }

        return raw;
    }

    /// <summary>
    /// Point a move at the end of one of its segments
    /// </summary>
    /// <param name="raw">The move, whose coordinates are overwritten</param>
    /// <param name="segments">What the move was broken into</param>
    /// <param name="segment">Which segment to prepare, counting from one</param>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>GCodes::ReadMove</c>. Each segment ends a fraction of the way along the
    /// line, and the last one ends exactly at the target rather than at the sum of the fractions -
    /// otherwise a long move would accumulate rounding and stop short of where it was asked to go.
    /// </para>
    /// <para>
    /// The bed compensation is applied here rather than to the move as a whole, which is the point of
    /// the mesh segment count: the correction depends on where the nozzle is, so following the bed
    /// means sampling it along the way. Applied once at the end it is a chord across the bed's shape
    /// </para>
    /// </remarks>
    public void PrepareSegment(RawMove raw, in SegmentedMove segments, int segment)
    {
        if (segment >= segments.Count)
        {
            segments.Target.AsSpan(0, segments.NumAxes).CopyTo(raw.Coords);
        }
        else
        {
            float fraction = (float)segment / segments.Count;
            for (int axis = 0; axis < segments.NumAxes; axis++)
            {
                raw.Coords[axis] = segments.Start[axis]
                    + ((segments.Target[axis] - segments.Start[axis]) * fraction);
                // CHECK RRF updates the MovementState.initialCoords (ie the start of the next segment). This is probably for pausing mid-segment because of the way RRF has multiple threads for the motion pipeline. Might not be necessary here
            }
        }

        // Limit the end position at each segment. This is needed for arc moves on any printer, and for [segmented] straight moves on SCARA printers.
        // TODO check the segment end position to see if it is valid for the current kinematics

        for (int drive = segments.FirstExtruderDrive; drive < MotionLimits.MaxAxesPlusExtruders; drive++)
        {
            raw.Coords[drive] = segments.ExtrusionPerSegment[drive];
        }

        if (raw.MoveType == MoveType.Normal)
        {
            AxisAndBedTransform(raw, segments.NumAxes);
        }

        // Each segment is its own move to the engine, so it needs its own correlation id
        raw.MoveId = 0;
    }

    /// <summary>
    /// Apply the corrections that turn a nominal machine position into the one the machine is driven to
    /// </summary>
    /// <param name="raw">The move being built</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <remarks>
    /// The skew first and the height map second, which is RepRapFirmware's
    /// <c>AxisAndBedTransform</c>: the map is indexed by coordinates the skew has already moved
    /// </remarks>
    public void AxisAndBedTransform(RawMove raw, int numAxes)
    {
        AxisSkew.Apply(currentTool(), model.Move, raw.Coords, numAxes);

        if (bedCompensation.AppliesTo(raw, numAxes))
        {
            bedCompensation.Apply(raw, numAxes);
        }
    }

    /// <summary>
    /// Work out how fast the move should go
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="input">The channel's interpreter state</param>
    /// <param name="raw">The move being built, with its move type and mentioned axes already set</param>
    /// <exception cref="GCodeException">The feed rate cannot be determined</exception>
    /// <remarks>
    /// Ported from <c>GCodes::LoadFeedrateFromGCode</c>. F persists across codes, so the value the
    /// user typed is kept on the channel - unconverted, because whether inches apply depends on the
    /// axes of the move it is eventually used for, which is not known when it is read
    /// </remarks>
    public void LoadFeedRate(DuetAPI.Commands.Code code, InputChannel input, RawMove raw)
    {
        // The overrides belong to the print, so they apply to an ordinary move that names an axis and
        // to nothing else
        raw.ApplyM220M221 = raw.MoveType == MoveType.Normal
            && (raw.LinearAxesMentioned || raw.RotationalAxesMentioned)
            && !code.Flags.HasFlag(CodeFlags.IsFromSystemMacro);
        // A G0 on a machine that is not printing is a rapid: it goes as fast as the machine can
        // rather than at the F the job last set, because on a mill or a laser F describes the cut and
        // a G0 is the move between cuts. On an FFF machine G0 honours F, which is what makes a travel
        // move take the speed a slicer chose for it
        bool isRapid = !raw.IsCoordinated && model.State.MachineMode != MachineMode.FFF;
        raw.UsingStandardFeedrate = !isRapid;

        // What the channel would use if the move named no F, kept unscaled and in mm/sec. Recorded
        // before anything below decides what this move actually travels at, because it is the file's
        // feed rate rather than this move's that a resume has to put back
        raw.OriginalFeedRateMmPerSec = ModalFeedRateMmPerSec(input);

        if (isRapid)
        {
            // RepRapFirmware's MaximumG0FeedRate, and the overrides do not apply to it - M220 scales
            // the print, and a rapid is not part of the print
            raw.FeedRateMmPerSec = MaximumG0FeedRate / SecondsPerMinute;
            raw.ApplyM220M221 = false;
            return;
        }

        if (input.InverseTimeMode)
        {
            // G93: F is one over the time the move should take, in minutes, so it is a duration and
            // not a speed. It cannot carry over from a previous move because it describes this move's
            // length, and it is not a distance, so the inch scale does not apply to it
            if (!code.TryGetFloat('F', out float inverseTime) || inverseTime <= 0.0f)
            {
                throw new GCodeException(
                    "Feed rate must be specified with every move when using inverse time mode");
            }

            // A duration, so the speed factor divides it rather than multiplying: M220 S200 should
            // make the move take half as long, not twice as long
            float duration = SecondsPerMinute / inverseTime;
            raw.DurationSec = raw.ApplyM220M221 ? duration / model.Move.SpeedFactor : duration;
            return;
        }

        if (code.TryGetFloat('F', out float feedRate))
        {
            // Kept raw, which is also what inputs[].feedRate reports
            input.FeedRate = feedRate;
            raw.OriginalFeedRateMmPerSec = ModalFeedRateMmPerSec(input);
        }

        // A move that names only rotational axes is measured in degrees, so G20 does not scale its
        // feed rate even though the same F would be inches per minute for a linear move
        bool convertInches = raw.LinearAxesMentioned || !raw.RotationalAxesMentioned;
        float unitScale = convertInches && input.DistanceUnit == DistanceUnit.Inch ? MmPerInch : 1.0f;
        float converted = input.FeedRate * unitScale / SecondsPerMinute;

        raw.FeedRateMmPerSec = raw.ApplyM220M221 ? converted * model.Move.SpeedFactor : converted;
    }

    /// <summary>
    /// The feed rate a channel is set to, in mm/sec
    /// </summary>
    /// <param name="input">The channel</param>
    /// <returns>The rate</returns>
    /// <remarks>
    /// The same conversion a restore point is written with, so that what a pause saves and what a
    /// resume puts back are the same quantity in both directions
    /// </remarks>
    private static float ModalFeedRateMmPerSec(InputChannel input)
        => input.FeedRate * (input.DistanceUnit == DistanceUnit.Inch ? MmPerInch : 1.0f) / SecondsPerMinute;

    /// <summary>
    /// How much of the code about to be built has already been done, 0..1
    /// </summary>
    /// <param name="code">The code</param>
    /// <returns>The fraction, which is zero for every code but one</returns>
    /// <remarks>
    /// <para>
    /// <see cref="MovementState.MoveFractionToSkip"/> is set when a job is resumed part-way through a
    /// code, and the code it describes is the first one the job file reads afterwards. The channel
    /// test is what keeps it from being spent on somebody else's move: the interpreter state is
    /// shared by every channel here - RepRapFirmware's is per motion system - so a
    /// <c>daemon.g</c> move landing in the window between the resume and the job's first code would
    /// otherwise be shortened instead.
    /// </para>
    /// <para>
    /// <c>File</c> and not <c>File2</c>, because there is one of this state and only the first file
    /// channel ever stores a fraction in it. Letting the second spend what the first is owed would be
    /// worse than not restoring it at all. TODO when M596 gives each motion system its own
    /// <see cref="MovementState"/>, both halves become per system together - the restore point that
    /// records the fraction as well as the move that spends it.
    /// </para>
    /// <para>
    /// The job file's own codes and not a macro's, by the same test that decides whether a move is
    /// recorded at all: a macro invoked between the resume and the job's next move runs on this
    /// channel too, and shortening its move would consume what the job is owed
    /// </para>
    /// </remarks>
    private float MoveFractionToSkipFor(DuetAPI.Commands.Code code)
        => JobMoveOrigin.IsJobFileCode(code) ? state.MoveFractionToSkip : 0.0f;

    /// <summary>
    /// Whether the code moves anything other than Z
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <returns>True if some axis other than Z was named</returns>
    public bool MentionsAxisOtherThanZ(DuetAPI.Commands.Code code, int numAxes)
    {
        for (int axis = 0; axis < numAxes; axis++)
        {
            Axis axisConfig = model.Move.Axes[axis];
            if (char.ToUpperInvariant(axisConfig.Letter) != 'Z' && code.HasParameter(axisConfig.Letter))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// How many pieces this move has to be broken into
    /// </summary>
    /// <param name="raw">The move, with its target already limited</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <returns>The segment count, at least one</returns>
    /// <remarks>
    /// <para>
    /// Ported from the segmentation block of <c>GCodes::DoStraightMove</c>. Three separate reasons a
    /// move may need splitting, and the answer is the largest of them, because each is a lower bound
    /// on what makes the move come out right:
    /// </para>
    /// <list type="bullet">
    /// <item>the geometry bows a straight line, so it has to be approximated by short ones;</item>
    /// <item>the height map has to be followed across the bed rather than applied at the ends;</item>
    /// <item>the move takes so long that the step clock would wrap during it.</item>
    /// </list>
    /// <para>
    /// RepRapFirmware makes the first of these optional and skips it while simulating. Here it is not
    /// optional: there is no local step generation to fall back on, so a move that is not segmented is
    /// simply executed as the wrong shape
    /// </para>
    /// </remarks>
    public int SegmentCountFor(RawMove raw, int numAxes)
    {
        KinematicsEngine geometry = Parameters.Geometry;

        // The move's own start, not the builder's. Both are where the last move left the machine,
        // but the builder's carries the bed correction and raw.Coords does not, so differencing the
        // two would count one mesh correction as movement
        ReadOnlySpan<float> start = raw.InitialCoords;

        // How far the move goes, counting the axes this geometry's error depends on
        float lengthSquared = 0.0f;
        for (int axis = 0; axis < numAxes && axis < 3; axis++)
        {
            bool counts = axis < 2 || geometry.Segmentation.HasFlag(SegmentationType.IncludeZ);
            if (counts)
            {
                float delta = raw.Coords[axis] - start[axis];
                lengthSquared += delta * delta;
            }
        }
        float length = MathF.Sqrt(lengthSquared);

        // TODO if machine type is a laser then we must use one segment per pixel

        int segments = 1;
        if (geometry.Segmentation.HasFlag(SegmentationType.Segment)
            && (raw.HasPositiveExtrusion || raw.IsCoordinated || geometry.Segmentation.HasFlag(SegmentationType.IncludeG0)))
        {
            // Short enough that the bow is below a step, but not so short that the move is chopped
            // into more pieces than the error justifies
            float speed = raw.InverseTimeMode
                ? (raw.DurationSec > 0.0f ? length / raw.DurationSec : 0.0f)
                : raw.FeedRateMmPerSec;
            float seconds = speed > 0.0f ? length / speed : 0.0f;

            float byLength = geometry.MinSegmentLength > 0.0f ? length / geometry.MinSegmentLength : 0.0f;
            float byTime = seconds * geometry.SegmentsPerSecond;
            segments = Math.Max(1, (int)MathF.Round(MathF.Min(byLength, byTime)));
        }

        if (bedCompensation.AppliesTo(raw, numAxes))
        {
            (float axis0, float axis1) = bedCompensation.GridCoordinates(raw.Coords, numAxes);
            (float startAxis0, float startAxis1) = bedCompensation.GridCoordinates(start, numAxes);
            segments = Math.Max(segments, bedCompensation.MinimumSegments(axis0 - startAxis0, axis1 - startAxis1));
        }

        // The step clock wraps roughly every 45 minutes, so a move that would take a large fraction
        // of that has to be split whatever the geometry says
        {
            float speed = raw.InverseTimeMode ? 0.0f : raw.FeedRateMmPerSec;
            float seconds = raw.InverseTimeMode
                ? raw.DurationSec
                : (speed > 0.0f ? length / speed : 0.0f);
            segments = Math.Max(segments, (int)(seconds / MaxSegmentSeconds));
        }

        return Math.Max(1, segments);
    }

    /// <summary>
    /// Axes the code names, as a bitmap
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <returns>The bitmap</returns>
    public uint AxesMentioned(DuetAPI.Commands.Code code, int numAxes)
    {
        uint mentioned = 0;
        for (int axis = 0; axis < numAxes && axis < MotionLimits.MaxAxes; axis++)
        {
            if (code.HasParameter(model.Move.Axes[axis].Letter))
            {
                mentioned |= 1u << axis;
            }
        }
        return mentioned;
    }

    /// <summary>
    /// Refuse a move that would touch an axis whose position is not known
    /// </summary>
    /// <param name="axesMentioned">Axes the code names, as a bitmap</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <exception cref="GCodeException">The move must not run</exception>
    /// <remarks>
    /// <para>
    /// Ported from <c>GCodes::CheckEnoughAxesHomed</c>. M564 S0 allows moving an unhomed axis, which
    /// is what makes a homing macro's own moves possible; the geometry gets to add to the set anyway,
    /// because on a delta or a SCARA the axes are not independently positioned and a coordinate in one
    /// of them means nothing until all of them are known.
    /// </para>
    /// <para>
    /// An extruder-only move is deliberately allowed either way - it names no axis, so there is
    /// nothing to be unsure of
    /// </para>
    /// </remarks>
    public void CheckEnoughAxesHomed(uint axesMentioned, int numAxes)
    {
        uint mustBeHomed = Parameters.Geometry.MustBeHomedAxes(axesMentioned, model.Move.NoMovesBeforeHoming);

        uint unhomed = 0;
        for (int axis = 0; axis < numAxes && axis < MotionLimits.MaxAxes; axis++)
        {
            if ((mustBeHomed & (1u << axis)) != 0 && !model.Move.Axes[axis].Homed)
            {
                unhomed |= 1u << axis;
            }
        }

        if (unhomed == 0)
        {
            return;
        }

        StringBuilder letters = new();
        for (int axis = 0; axis < numAxes && axis < MotionLimits.MaxAxes; axis++)
        {
            if ((unhomed & (1u << axis)) != 0)
            {
                letters.Append(model.Move.Axes[axis].Letter);
            }
        }
        throw new GCodeException($"Insufficient axes homed ({letters})");
    }

    /// <summary>
    /// Bring a move within what the machine can reach, or refuse it
    /// </summary>
    /// <param name="raw">The move being built, whose coordinates may be adjusted</param>
    /// <param name="axesRelative">Whether the move was commanded relative to where the machine is</param>
    /// <param name="axesMentioned">Axes the code names, as a bitmap</param>
    /// <param name="hasForwardExtrusion">Whether the move extrudes, so its path is being printed</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <exception cref="GCodeException">The move cannot be made possible</exception>
    /// <remarks>
    /// <para>
    /// Ported from the <c>LimitPosition</c> block of <c>GCodes::DoStraightMove</c>. Only axes that are
    /// both homed and actually moving are limited. RepRapFirmware gives two reasons and both apply
    /// here: an axis whose position is not known has nothing to limit against, and limiting an axis
    /// the move does not touch could turn a rotational-only move into one that moves a linear axis
    /// too, which the planner would then throw away as having no movement.
    /// </para>
    /// <para>
    /// Whether an unreachable target is an error or is quietly clamped depends on how it was asked
    /// for. An absolute move names a place, and moving somewhere else instead would be wrong; a
    /// relative move names a direction, so going as far as the machine can is the sensible reading.
    /// </para>
    /// <para>
    /// The last resort is for a target that is reachable by a path other than a straight line, which
    /// is a delta near the top of its travel or a SCARA passing its inner radius. A travel move can
    /// simply be uncoordinated - the axes each go their own way and the head takes some curve - but a
    /// printing move cannot, because the curve would be extruded
    /// </para>
    /// </remarks>
    public void LimitPosition(RawMove raw, bool axesRelative, uint axesMentioned,
                              bool hasForwardExtrusion, int numAxes)
    {
        // CHECK this logic is comparable to RRF in `GCodes::DoStraightMove()`
        uint axesToLimit = 0;
        for (int axis = 0; axis < numAxes && axis < MotionLimits.MaxAxes; axis++)
        {
            if ((axesMentioned & (1u << axis)) != 0 && model.Move.Axes[axis].Homed)
            {
                axesToLimit |= 1u << axis;
            }
        }

        KinematicsEngine geometry = Parameters.Geometry;

        // RepRapFirmware passes ms.initialCoords here, which is the uncompensated start - the same
        // space raw.Coords is still in at this point. The error from using the builder's compensated
        // copy is only one mesh correction in Z, but it is the same conflation
        ReadOnlySpan<float> initialCoords = raw.InitialCoords.AsSpan(0, numAxes);

        LimitPositionResult result = geometry.LimitPosition(
            raw.Coords.AsSpan(0, numAxes), initialCoords, numAxes, axesToLimit,
            raw.IsCoordinated, model.Move.LimitAxes);

        if (result is LimitPositionResult.Adjusted or LimitPositionResult.AdjustedAndIntermediateUnreachable)
        {
            if (!axesRelative)
            {
                throw new GCodeException("Target position not reachable");
            }

            // The move was clamped, so the interpreter has to be told where it is really going or the
            // next relative move would be measured from a position the machine never reached
            SyncInterpreterToTarget(raw, numAxes);

            if (result == LimitPositionResult.Adjusted)
            {
                return;
            }
        }

        if (result is LimitPositionResult.IntermediateUnreachable or LimitPositionResult.AdjustedAndIntermediateUnreachable)
        {
            bool canGoRoundTheHouses = raw.IsCoordinated && !hasForwardExtrusion;
            if (canGoRoundTheHouses)
            {
                LimitPositionResult uncoordinated = geometry.LimitPosition(
                    raw.Coords.AsSpan(0, numAxes), initialCoords, numAxes, axesToLimit,
                    isCoordinated: false, model.Move.LimitAxes);
                if (uncoordinated == LimitPositionResult.Ok)
                {
                    raw.IsCoordinated = false;
                    return;
                }
            }
            throw new GCodeException("Target position not reachable from current position");
        }
    }

    /// <summary>
    /// Bring the interpreter's position into step with a target that was clamped
    /// </summary>
    /// <param name="raw">The move, whose coordinates are now what the machine will do</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <remarks>
    /// RepRapFirmware's <c>ToolOffsetInverseTransform</c> after a limit was applied, and one of the
    /// few places the transform is inverted. Neither the bed compensation nor the skew has been
    /// applied at this point - both happen per segment - so the tool transform is the only one to
    /// undo, which is why this is not the handler's <c>SyncInterpreterToMachine</c>
    /// </remarks>
    public void SyncInterpreterToTarget(RawMove raw, int numAxes)
    {
        raw.Coords.AsSpan(0, numAxes).CopyTo(state.CurrentUserPosition);
        ToolTransform.Remove(currentTool(), model.Move, state.CurrentUserPosition, numAxes);
    }

    /// <summary>
    /// Bring the interpreter's position back into step with where the machine actually is
    /// </summary>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>ToolOffsetInverseTransform</c> after a homing or probing move, and the
    /// only place the whole transform is inverted. Everywhere else the interpreter is authoritative
    /// and the machine follows it; here the machine is somewhere the interpreter did not put it.
    /// That is homing, probing, and a feedhold, which stops the machine before the moves the
    /// interpreter had already built have run.
    /// </para>
    /// <para>
    /// The bed transform is undone first, as RepRapFirmware's <c>InverseBedTransform</c> is before
    /// <c>ToolOffsetInverseTransform</c>. The builder's position is where the machine was
    /// <em>commanded</em>, correction included, so leaving the correction in would hand the
    /// interpreter a Z that is already compensated - and it would then be compensated a second time
    /// on the next move. The caller must hold the object model write lock and the planner lock, and
    /// is what publishes the result
    /// </para>
    /// </remarks>
    public void SyncInterpreterToMachine()
    {
        int numAxes = Parameters.SharedAxisCount(model.Move);
        builder.StartCoordinates[..numAxes].CopyTo(state.CurrentUserPosition);

        // The bed transform first and the axis transform second, which is the order RepRapFirmware's
        // InverseAxisAndBedTransform uses - the mirror of applying the axis transform before the bed
        // one, because the map is indexed by coordinates the skew has already moved
        bedCompensation.Remove(state.CurrentUserPosition, numAxes);
        AxisSkew.Remove(currentTool(), model.Move, state.CurrentUserPosition, numAxes);
        ToolTransform.Remove(currentTool(), model.Move, state.CurrentUserPosition, numAxes);
    }

    /// <summary>
    /// Fill in where a special move starts from
    /// </summary>
    /// <param name="raw">The move being built</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <remarks>
    /// Ported from the <c>moveType != 0</c> block of <c>GCodes::DoStraightMove</c>. A raw motor move
    /// is measured in motor positions, so it starts from the motor endpoints converted back to mm per
    /// drive; anything else is still an axis move and starts from the axis coordinates. Both come
    /// from the builder rather than the object model, because the builder's copy is where the last
    /// queued move left the machine and the object model's is where the machine has got to
    /// </remarks>
    public void SeedSpecialMoveCoordinates(RawMove raw, int numAxes)
    {
        for (int axis = 0; axis < numAxes; axis++)
        {
            SeedSpecialMoveCoordinate(raw, axis);
        }
    }

    /// <summary>
    /// Fill in where one axis of a special move starts from
    /// </summary>
    /// <param name="raw">The move being built</param>
    /// <param name="axis">The axis</param>
    /// <remarks>
    /// One axis of <see cref="SeedSpecialMoveCoordinates"/>, because holding an axis still means
    /// putting back exactly what the seed had left in its slot
    /// </remarks>
    public void SeedSpecialMoveCoordinate(RawMove raw, int axis)
    {
        MotionParameters parameters = Parameters;
        if (parameters.Geometry.IsRawMotorMove(raw.MoveType))
        {
            float stepsPerMm = parameters.StepsPerMm[axis];
            raw.Coords[axis] = stepsPerMm != 0.0f ? builder.EndPoints[axis] / stepsPerMm : 0.0f;
        }
        else
        {
            raw.Coords[axis] = builder.StartCoordinates[axis];
        }
    }

    /// <summary>
    /// Read the E parameter into a move
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="input">The channel's interpreter state</param>
    /// <param name="raw">Move to fill in</param>
    /// <param name="unitScale">Millimetres per user unit</param>
    /// <param name="moveFraction">How much of the move is still to do, 1 unless it is being resumed</param>
    /// <returns>True if the move extrudes forwards, which is what pressure advance applies to</returns>
    /// <exception cref="GCodeException">The extrusion cannot be applied</exception>
    /// <remarks>The caller must hold the object model lock</remarks>
    public bool ApplyExtrusion(DuetAPI.Commands.Code code, InputChannel input, RawMove raw, float unitScale,
                               float moveFraction = 1.0f)
    {
        bool hasExtrusion = false;
        raw.HasPositiveExtrusion = false;

        if (!code.TryGetFloatArray('E', out float[]? extrusion) || extrusion.Length == 0)
        {
            return false;
        }

        // A tool is what has extruders, so an E word with none selected has nothing to address.
        // RepRapFirmware refuses rather than extruding on the first drive it can find, because a
        // slicer that emits E before T is describing a print for a machine it thinks is set up
        Tool? tool = currentTool();
        if (tool is null || tool.Extruders.Count == 0)
        {
            throw new GCodeException("Attempting to extrude with no tool selected");
        }

        int numExtruders = Parameters.SharedExtruderCount(model.Move);

        // Either one value per drive of the tool, or a single value the mix ratios fan out across
        // them. Which of the two it is decides what the numbers mean, so a count that matches
        // neither is a mistake rather than something to interpret
        bool mixing = extrusion.Length == 1;
        if (!mixing && extrusion.Length != tool.Extruders.Count)
        {
            throw new GCodeException(
                $"Wrong number of extrusion values: tool {tool.Number} has {tool.Extruders.Count} drives");
        }

        for (int index = 0; index < tool.Extruders.Count; index++)
        {
            int extruder = tool.Extruders[index];
            if (extruder < 0 || extruder >= numExtruders)
            {
                continue;                       // the tool names a drive this machine does not have
            }

            Extruder extruderConfig = model.Move.Extruders[extruder];

            // A mixing tool splits one E value between its drives by the ratios M567 set, so the
            // slicer commands the filament the nozzle consumes and the machine decides where it
            // comes from
            float share = mixing ? MixRatio(tool, index) : 1.0f;
            float requestedMm = extrusion[mixing ? 0 : index] * unitScale * share;

            // Absolute extrusion is a running total, so the movement is the difference from where
            // the extruder was last told it had reached
            float movement = input.DrivesRelative ? requestedMm : requestedMm - extruderConfig.RawPosition;

            // Extrusion is an amount however the file expresses it, so a move that is already part
            // done owes only the rest of it. This is what RepRapFirmware gets by skipping whole
            // segments of the re-read move and scaling the one it restarts inside.
            //
            // Why this scales where an absolute *axis* target does not: the resume puts the axes
            // right by moving their start, back to where the machine stopped, so what the line names
            // is already the rest of the move. An extruder has no such start to move - the resync is
            // axes-only, because the engine carries the fraction of a step between moves - so
            // RepRapFirmware moves the reference the other way instead, rewinding
            // latestVirtualExtruderPosition to the extruder position at the *start* of the
            // interrupted line (RestorePoint's virtualExtruderPosition). The difference below is
            // then the whole line's extrusion, and this is the part of it still owed - which makes
            // the two E modes behave identically, as they must.
            //
            // TODO that rewind does not happen yet, because nothing tracks the absolute extruder
            // position at all: RawPosition is only ever written by G92 E and
            // RestorePoint.VirtualExtruderPosition is hardwired to zero (§15.2). Whoever lands that
            // tracking has to restore it here to the value at the start of the interrupted line, not
            // to where the machine stopped - restoring the stop point would count the same filament
            // twice, once in the difference and once in this scale factor
            movement *= moveFraction;
            if (movement != 0.0f)
            {
                hasExtrusion = true;
                if (movement > 0.0f)
                {
                    raw.HasPositiveExtrusion = true;
                }

                // TODO handle volumetric extrusion (M200), which scales this by the filament's
                // cross-section - move.extruders[].filamentDiameter is in the object model and
                // nothing writes or reads it
            }

            // M221 is the operator adjusting a print, so it applies to the same moves M220 does
            raw.Coords[MotionParameters.ExtruderToDrive(extruder)] =
                raw.ApplyM220M221 ? movement * extruderConfig.Factor : movement;

            // TODO track rawExtruderTotal and the virtual extruder position, which is what print
            // progress and move.motionSystems[].virtualEPos report - §15.2
        }

        // TODO extruder endstops (G1 H1 E), which need the per-extruder speed calculation
        return hasExtrusion;
    }

    /// <summary>
    /// The share of a mixing tool's extrusion that one of its drives takes
    /// </summary>
    /// <param name="tool">The tool</param>
    /// <param name="index">Which of its drives, by position rather than extruder number</param>
    /// <returns>The ratio</returns>
    /// <remarks>
    /// A tool defined before M567 gets an even split, which is what <see cref="Tools.ToolManager"/>
    /// fills in. A ratio missing entirely means the tool has more drives than ratios, which M567
    /// refuses, so this is a bound rather than a policy
    /// </remarks>
    private static float MixRatio(Tool tool, int index)
        => index < tool.Mix.Count ? tool.Mix[index] : 0.0f;

    /// <summary>
    /// Say which endstop stops which drive of a homing move
    /// </summary>
    /// <param name="plans">What each axis the code named watches</param>
    /// <param name="raw">The move being built</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <remarks>
    /// <para>
    /// RepRapFirmware picks one of three actions when an endstop fires, and which one depends on the
    /// geometry rather than on the endstop. If moving an axis needs drives other than its own - X on
    /// a CoreXY needs both motors - then stopping only that axis' drivers would leave the others
    /// running and drag the head diagonally into the switch, so the whole move has to stop. That is
    /// RRF's <c>stopAll</c>, and it is decided by exactly the test below. Otherwise the axis is
    /// independent and stopping its own drivers is enough, which is <c>stopAxis</c>.
    /// </para>
    /// <para>
    /// The plans cover only the axes the code named, so only those are armed: a homing move naming X
    /// and Y must not be stopped by Z's switch happening to be closed already. They were worked out
    /// before the boards were told what to watch for, which is what makes the drivers armed over the
    /// bus and the drivers named in the move the same drivers
    /// </para>
    /// </remarks>
    public void ApplyEndstops(IReadOnlyList<EndstopPlan> plans, RawMove raw, int numAxes)
    {
        // What stopped the last endstop move says nothing about this one. Cleared here rather than
        // where the move finishes, so that a move which is never reported as stopped - because it
        // ran its full length - leaves an empty latch rather than the previous move's
        state.ArmEndstops();

        // Every rule about what stops what is in EndstopArming; this applies what it decided. The
        // holding is here because it writes the move's coordinates from the machine position, which
        // is the interpreter's business rather than the endstops'
        ArmedMove armed = EndstopArming.Arm(model.Move, Parameters.Geometry, numAxes, plans,
                                            closedEndstopSwitches, raw.StopOnInput);

        raw.ArmedAxes.AddRange(armed.ArmedAxes);
        raw.ReduceAcceleration |= armed.ReduceAcceleration;
        raw.SharesSwitchesAcrossDrives = armed.SharesSwitchesAcrossDrives;
        foreach (int axis in armed.AxesToHold)
        {
            SeedSpecialMoveCoordinate(raw, axis);
        }

        // An axis commanded to stay where it is never moves, so no input changes and no stop is ever
        // reported - and yet the axis is at its switch, which is the whole question a homing move
        // asks. RepRapFirmware arrives at the same answer by a different route: its step interrupt
        // tests the endstop before the first step, so the move stops on the step it began and the
        // stop is recorded like any other
        state.RecordEndstopTriggered(armed.TriggeredAxes);
        ArmCorrection(raw);
    }

    /// <summary>
    /// Tell the endstop correction what this move watches, and which motors are already down
    /// </summary>
    /// <param name="raw">The move, with its stop inputs settled</param>
    /// <remarks>
    /// <para>
    /// Read back from the move rather than taken from what the arming decided, because the move is
    /// what the controller and the boards will act on: coupled kinematics rewrite every drive's stop
    /// input to the one switch, and an axis whose endstop was already closed is held rather than
    /// armed. Reading the move is what makes it impossible for the correction's idea of the move to
    /// drift from the one that was actually sent.
    /// </para>
    /// <para>
    /// A motor held because it was already on its switch is given no steps, so it never moves and no
    /// stop is ever reported for it. It counts as stopped from the start, or the drive would wait
    /// for a report that cannot arrive and the move would run its full length instead of ending when
    /// the last moving motor reaches its own switch
    /// </para>
    /// </remarks>
    public void ArmCorrection(RawMove raw)
    {
        uint armedDrives = 0;
        for (int drive = 0; drive < raw.StopOnInput.Length && drive < MotionLimits.MaxAxesPlusExtruders; drive++)
        {
            if (raw.StopOnInput[drive].NumSwitches > 0)
            {
                armedDrives |= 1u << drive;
            }
        }
        endstopCorrection.ArmMove(armedDrives);

        // A motor held because it was already on its switch is given no steps, so it never moves and
        // no stop is ever reported for it. It counts as stopped from the start, or the drive would
        // wait for a report that cannot arrive and the move would run its full length instead of
        // ending when the last moving motor reaches its own switch. Seeded after ArmMove, which is
        // what clears the record of the previous move
        for (int drive = 0; drive < raw.StopOnInput.Length && drive < MotionLimits.MaxAxesPlusExtruders; drive++)
        {
            byte held = raw.StopOnInput[drive].HeldDrivers;
            while (held != 0)
            {
                int driverIndex = BitOperations.TrailingZeroCount(held);
                endstopCorrection.NoteDriverAlreadyStopped(drive, driverIndex);
                held &= (byte)(held - 1);
            }
        }
    }

    /// <summary>
    /// The workplace offset in effect for an axis
    /// </summary>
    /// <param name="axis">The axis</param>
    /// <param name="workplace">Selected workplace number</param>
    /// <returns>The offset in mm</returns>
    public static float WorkplaceOffset(Axis axis, int workplace)
        => workplace >= 0 && workplace < axis.WorkplaceOffsets.Count ? axis.WorkplaceOffsets[workplace] : 0.0f;

    /// <summary>
    /// The selected workplace, which is a property of the motion system rather than of the machine
    /// </summary>
    /// <remarks>
    /// Only the first motion system is read, as everywhere else here: several of them is a
    /// RepRapFirmware feature that has not been ported, so there is never more than one
    /// </remarks>
    public int WorkplaceNumber
        => model.Move.MotionSystems.Count > 0 ? model.Move.MotionSystems[0].WorkplaceNumber : 0;
}
