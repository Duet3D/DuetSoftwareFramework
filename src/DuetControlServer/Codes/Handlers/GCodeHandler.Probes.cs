using DuetAPI.ObjectModel;
using DuetControlServer.Motion;
using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// The Z probe G-codes
/// </summary>
internal sealed partial class GCodeHandler
{
    /// <summary>
    /// G31: set or report the trigger height, offsets and threshold of a Z probe
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// The Z parameter is the trigger height, which is the negative of the probe's Z offset. Both are
    /// stored, as RepRapFirmware stores both, because a user reads the trigger height and the
    /// kinematics read the offset
    /// </remarks>
    private async ValueTask<Message?> HandleProbeParametersAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        int probeNumber = code.GetInt('K', 0);
        if (probeNumber < 0 || probeNumber >= RemoteProbes.MaxProbes)
        {
            return new Message(MessageType.Error, $"Z probe number out of range (0..{RemoteProbes.MaxProbes - 1})");
        }

        string? report = null;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Probe? probe = probeNumber < model.Sensors.Probes.Count ? model.Sensors.Probes[probeNumber] : null;
            if (probe is null)
            {
                return new Message(MessageType.Error, $"Z probe {probeNumber} not found");
            }

            bool seen = false;

            // One offset per axis, as in RepRapFirmware, so a machine with more than three axes can
            // say where the probe sits on each of them
            while (probe.Offsets.Count < model.Move.Axes.Count)
            {
                probe.Offsets.Add(0.0f);
            }

            for (int axis = 0; axis < model.Move.Axes.Count; axis++)
            {
                char letter = model.Move.Axes[axis].Letter;
                if (letter == 'Z')
                {
                    continue;                   // Z is the trigger height, handled below
                }

                if (code.TryGetFloat(letter, out float offset))
                {
                    probe.Offsets[axis] = offset;
                    seen = true;
                }
            }

            if (code.TryGetFloat('Z', out float triggerHeight))
            {
                probe.TriggerHeight = triggerHeight;
                seen = true;
            }

            // Whichever way round they were given, the two have to agree: the offset is what a move
            // is corrected by and the trigger height is what a user reads back
            int zAxis = ZAxisIndex(model.Move);
            if (zAxis >= 0 && zAxis < probe.Offsets.Count)
            {
                probe.Offsets[zAxis] = -probe.TriggerHeight;
            }

            if (code.TryGetInt('P', out int threshold))
            {
                probe.Threshold = threshold;
                seen = true;
            }

            if (code.TryGetInt('H', out int sensor))
            {
                probe.Sensor = sensor;
                seen = true;
            }

            if (code.TryGetFloatArray('T', out float[]? coefficients))
            {
                for (int i = 0; i < probe.TemperatureCoefficients.Count; i++)
                {
                    probe.TemperatureCoefficients[i] = i < coefficients.Length ? coefficients[i] : 0.0f;
                }
                seen = true;
            }

            if (code.TryGetFloat('S', out float calibrationTemperature))
            {
                probe.CalibrationTemperature = calibrationTemperature;
                seen = true;
            }

            if (!seen)
            {
                report = DescribeProbeParameters(probeNumber, probe, model.Move);
            }
        }
        return report is null ? new Message() : new Message(MessageType.Success, report);
    }

    /// <summary>
    /// Which axis is Z, or -1 if the machine has none
    /// </summary>
    /// <param name="move">The move model</param>
    /// <returns>The axis index</returns>
    /// <remarks>The caller must hold the object model lock</remarks>
    private static int ZAxisIndex(Move move)
    {
        for (int axis = 0; axis < move.Axes.Count; axis++)
        {
            if (move.Axes[axis].Letter == 'Z')
            {
                return axis;
            }
        }
        return -1;
    }

    /// <summary>
    /// Describe a probe the way G31 with no parameters does
    /// </summary>
    /// <param name="probeNumber">Probe number</param>
    /// <param name="probe">The probe</param>
    /// <param name="move">The move model, for the axis letters</param>
    /// <returns>The description</returns>
    /// <remarks>The caller must hold the object model lock</remarks>
    private static string DescribeProbeParameters(int probeNumber, Probe probe, Move move)
    {
        StringBuilder builder = new();
        int reading = probe.Value.Count > 0 ? probe.Value[0] : 0;
        builder.Append(CultureInfo.InvariantCulture, $"Z probe {probeNumber}: current reading {reading}");
        builder.Append(CultureInfo.InvariantCulture, $", threshold {probe.Threshold}");
        builder.Append(CultureInfo.InvariantCulture, $", trigger height {probe.TriggerHeight:F3}");
        if (probe.TemperatureCoefficients[0] != 0.0f || probe.TemperatureCoefficients[1] != 0.0f)
        {
            builder.Append(CultureInfo.InvariantCulture,
                           $" at {probe.CalibrationTemperature:F1}C, temperature coefficients ["
                           + $"{probe.TemperatureCoefficients[0]:F1}/C, {probe.TemperatureCoefficients[1]:F1}/C^2]");
        }

        builder.Append(", offsets");
        for (int axis = 0; axis < move.Axes.Count && axis < probe.Offsets.Count; axis++)
        {
            if (move.Axes[axis].Letter != 'Z')
            {
                builder.Append(CultureInfo.InvariantCulture, $" {move.Axes[axis].Letter}{probe.Offsets[axis]:F1}");
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// Raise or lower a move's Z target by however much the bed deviates under it
    /// </summary>
    /// <param name="move">The move being built</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <remarks>
    /// The map is measured over two named axes rather than always X and Y, so the coordinates fed to
    /// it are looked up by letter. RepRapFirmware does the same, which is what lets an IDEX machine
    /// map its U axis
    /// </remarks>
    private void ApplyBedCompensation(RawMove move, int numAxes)
    {
        if (!bedCompensation.IsActive)
        {
            return;
        }

        int zAxis = ZAxisIndex(model.Move);
        if (zAxis < 0 || zAxis >= numAxes)
        {
            return;                             // nothing to correct on a machine with no Z
        }

        (float axis0, float axis1) = GridCoordinates(move, numAxes);
        move.Coords[zAxis] += bedCompensation.GetCorrection(axis0, axis1, move.Coords[zAxis]);
    }

    // The bed correction used to be taken back off a committed move to recover the coordinate the
    // user asked for. It no longer is: the interpreter keeps its own position, so what was asked for
    // is still known and does not have to be reconstructed. That is the point of MovementState -
    // GetRequestedHeight is only an approximate inverse, because the correction depends on where the
    // nozzle ends up, which is what the correction is being removed from

    /// <summary>
    /// How many segments the height map needs a move broken into
    /// </summary>
    /// <param name="deltaAxis0">Movement along the map's first axis, mm</param>
    /// <param name="deltaAxis1">Movement along its second, mm</param>
    /// <returns>The minimum number of segments</returns>
    /// <remarks>
    /// RepRapFirmware's <c>HeightMap::GetMinimumSegments</c>: two segments per grid cell crossed, so
    /// the correction is sampled inside each cell rather than only where the move happens to end. A
    /// correction applied at the ends alone is a chord across the bed's actual shape
    /// </remarks>
    private int MeshSegments(float deltaAxis0, float deltaAxis1)
    {
        ProbeGrid grid = model.Move.Compensation.LiveGrid ?? model.Move.Compensation.ProbeGrid;
        if (grid.Spacings.Count < 2)
        {
            return 1;
        }

        int Segments(float distance, float spacing)
            => spacing > 0.0f ? (int)(2.0f * MathF.Abs(distance) / spacing) + 1 : 1;

        return Math.Max(Segments(deltaAxis0, grid.Spacings[0]), Segments(deltaAxis1, grid.Spacings[1]));
    }

    /// <summary>
    /// Whether the height map applies to this move at all
    /// </summary>
    /// <param name="move">The move</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <returns>True if the correction will be applied</returns>
    /// <remarks>
    /// RepRapFirmware's <c>IsUsingMeshCompensation</c>. Above the taper height the correction has
    /// faded to nothing, so there is nothing to follow and nothing to segment for
    /// </remarks>
    private bool IsUsingMeshCompensation(RawMove move, int numAxes)
    {
        if (!bedCompensation.IsActive)
        {
            return false;
        }

        int zAxis = ZAxisIndex(model.Move);
        return zAxis < 0 || zAxis >= numAxes || bedCompensation.AppliesAt(move.Coords[zAxis]);
    }

    /// <summary>
    /// The height map's two coordinates for an arbitrary position
    /// </summary>
    /// <param name="coords">Axis coordinates</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <returns>The pair the map is indexed by</returns>
    private (float Axis0, float Axis1) GridCoordinatesOf(ReadOnlySpan<float> coords, int numAxes)
    {
        float[] coordinates = [0.0f, 0.0f];
        ProbeGrid grid = model.Move.Compensation.LiveGrid ?? model.Move.Compensation.ProbeGrid;

        for (int i = 0; i < 2; i++)
        {
            for (int axis = 0; axis < numAxes && axis < coords.Length; axis++)
            {
                if (model.Move.Axes[axis].Letter == grid.Axes[i])
                {
                    coordinates[i] = coords[axis];
                    break;
                }
            }
        }
        return (coordinates[0], coordinates[1]);
    }

    /// <summary>
    /// Where a move ends up, in the two axes the height map is measured over
    /// </summary>
    /// <param name="move">The move</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <returns>The two coordinates</returns>
    private (float Axis0, float Axis1) GridCoordinates(RawMove move, int numAxes)
    {
        float[] coordinates = [0.0f, 0.0f];
        ProbeGrid grid = model.Move.Compensation.LiveGrid ?? model.Move.Compensation.ProbeGrid;

        for (int i = 0; i < 2; i++)
        {
            for (int axis = 0; axis < numAxes; axis++)
            {
                if (model.Move.Axes[axis].Letter == grid.Axes[i])
                {
                    coordinates[i] = move.Coords[axis];
                    break;
                }
            }
        }
        return (coordinates[0], coordinates[1]);
    }
}
