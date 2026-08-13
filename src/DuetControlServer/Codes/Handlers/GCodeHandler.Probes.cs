using DuetAPI.ObjectModel;
using DuetControlServer.Motion;
using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static DuetControlServer.Motion.AxisIndices;

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
    private async ValueTask<Message> HandleProbeParametersAsync(Commands.Code code, CancellationToken cancellationToken)
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
}
