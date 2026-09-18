using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPI.Commands;
using DuetControlServer.Files;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// M500: write the settings a machine calibrates for itself into config-override.g
/// </summary>
/// <remarks>
/// <para>
/// Ported from RepRapFirmware's <c>WriteConfigOverrideFile</c>, and deliberately no wider than it.
/// config-override.g holds the values a machine <em>discovers</em> - what its heaters turned out to
/// behave like, where its probe actually triggers, how far its axes really travel - so that a
/// re-flashed machine does not have to be re-tuned. It is not a dump of the configuration: config.g
/// is that, and it is written by hand.
/// </para>
/// <para>
/// Writing the whole object model back out is a different feature and belongs after the migration,
/// not inside it. Doing it now would mean deciding the format of every setting before the code that
/// sets it has been ported, which is the order that produces a format nothing quite fits
/// </para>
/// </remarks>
internal partial class MCodeHandler
{
    /// <summary>
    /// The extra sections M500's P parameter asks for
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's own numbering: the value is the code whose output is being asked for, so P10
    /// means "include the G10 offsets" and P31 "include the G31 probe values". Both are things that
    /// are normally only written once they have been measured, and P says to write them anyway
    /// </remarks>
    private const int SaveToolOffsets = 10, SaveProbeValues = 31;

    /// <summary>
    /// M500: save the calibrated settings
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleSaveConfigOverrideAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        bool saveToolOffsets = false, saveProbeValues = false;
        if (code.TryGetIntArray('P', out int[]? requested))
        {
            foreach (int value in requested)
            {
                // Anything else is ignored without a warning, as in RepRapFirmware: the parameter is
                // a list of opt-ins and an unknown one is a newer firmware's, not a mistake
                saveToolOffsets |= value == SaveToolOffsets;
                saveProbeValues |= value == SaveProbeValues;
            }
        }

        StringBuilder builder = new();
        builder.AppendLine(CultureInfo.InvariantCulture,
                           $"; config-override.g file generated in response to M500 at {DateTime.Now:yyyy-MM-dd HH:mm}");
        builder.AppendLine("; This is a system-generated file - do not edit");

        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            AppendKinematicsCalibration(builder);
            AppendHeaterModels(builder);
            AppendProbedAxisLimits(builder);
            AppendProbeValues(builder, saveProbeValues);
            AppendToolOffsets(builder, saveToolOffsets);
            AppendWorkplaceOffsets(builder);
        }

        string physicalPath = await filePathResolver.ToPhysicalAsync(FilePathResolver.ConfigOverrideFile,
                                                                     FileDirectory.System, cancellationToken);
        try
        {
            await File.WriteAllTextAsync(physicalPath, builder.ToString(), cancellationToken);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not write {File}", FilePathResolver.ConfigOverrideFile);
            return new Message(MessageType.Error,
                               $"Failed to write file {FilePathResolver.ConfigOverrideFile}: {e.Message}");
        }

        // TODO warn when config.g contains no M501, as RepRapFirmware does. It tracks that with
        // m501SeenInConfigFile, set only while config.g is the file being run; nothing here knows
        // which macro a code came from, and a flag meaning "M501 ran at some point" would stay quiet
        // in exactly the case the warning exists for - a machine that saves settings it never loads
        return new Message();
    }

    /// <summary>
    /// The geometry's own calibration, which is what an auto-calibration produced
    /// </summary>
    /// <remarks>
    /// A delta's calibration is the whole point of config-override.g: the tower positions and endstop
    /// corrections G32 works out cannot be known in advance, so config.g cannot contain them. The
    /// geometry reports itself in the form M665 and M666 take, which is the same report a bare M665
    /// gives - reporting from the authoritative side, as §14.4 requires
    /// </remarks>
    private void AppendKinematicsCalibration(StringBuilder builder)
    {
        StringBuilder report = new();
        bool any = false;
        foreach (int mCode in (int[])[665, 666, 669])
        {
            report.Clear();
            if (planner.Parameters.Geometry.AppendReport(report, mCode))
            {
                if (!any)
                {
                    builder.AppendLine("; Kinematics parameters");
                    any = true;
                }
                builder.AppendLine(report.ToString());
            }
        }
    }

    /// <summary>
    /// The heater models, which is what M303 tuning produced
    /// </summary>
    private void AppendHeaterModels(StringBuilder builder)
    {
        bool any = false;
        for (int heaterNumber = 0; heaterNumber < model.Heat.Heaters.Count; heaterNumber++)
        {
            if (model.Heat.Heaters[heaterNumber] is not Heater heater)
            {
                continue;
            }

            if (!any)
            {
                builder.AppendLine("; Heater model parameters");
                any = true;
            }

            HeaterModel heaterModel = heater.Model;
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"M307 H{heaterNumber} R{heaterModel.HeatingRate:F3} "
                + $"C{(heaterModel.CoolingRate > 0.0f ? 1.0f / heaterModel.CoolingRate : 0.0f):F1} "
                + $"D{heaterModel.DeadTime:F2} S{heaterModel.MaxPwm:F2} "
                + $"V{heaterModel.StandardVoltage:F1} B{(heaterModel.PID.Used ? 0 : 1)}");
        }
    }

    /// <summary>
    /// Axis limits that were measured rather than configured
    /// </summary>
    /// <remarks>
    /// Only the axes a <c>G1 H3</c> actually measured. An axis whose limit came from config.g's M208
    /// is already in config.g, and writing it again would mean the override silently winning if the
    /// two ever disagreed
    /// </remarks>
    private void AppendProbedAxisLimits(StringBuilder builder)
    {
        AppendAxisLimits(builder, minima: true);
        AppendAxisLimits(builder, minima: false);
    }

    private void AppendAxisLimits(StringBuilder builder, bool minima)
    {
        StringBuilder line = new();
        for (int axis = 0; axis < model.Move.Axes.Count; axis++)
        {
            Axis axisConfig = model.Move.Axes[axis];
            if (minima ? axisConfig.MinProbed : axisConfig.MaxProbed)
            {
                line.Append(CultureInfo.InvariantCulture,
                            $" {axisConfig.Letter}{(minima ? axisConfig.Min : axisConfig.Max):F2}");
            }
        }

        if (line.Length > 0)
        {
            builder.AppendLine("; Probed axis limits");
            builder.AppendLine(CultureInfo.InvariantCulture, $"M208 S{(minima ? 1 : 0)}{line}");
        }
    }

    /// <summary>
    /// The Z probe trigger heights and offsets
    /// </summary>
    /// <param name="builder">Destination</param>
    /// <param name="always">Whether to write them even where they were not measured</param>
    private void AppendProbeValues(StringBuilder builder, bool always)
    {
        bool any = false;
        for (int probeNumber = 0; probeNumber < model.Sensors.Probes.Count; probeNumber++)
        {
            if (model.Sensors.Probes[probeNumber] is not Probe probe)
            {
                continue;
            }
            if (!always && probe.Type == ProbeType.None)
            {
                continue;
            }

            if (!any)
            {
                builder.AppendLine("; Z probe parameters");
                any = true;
            }

            StringBuilder line = new();
            line.Append(CultureInfo.InvariantCulture, $"G31 K{probeNumber} P{probe.Threshold}");
            for (int axis = 0; axis < model.Move.Axes.Count && axis < probe.Offsets.Count; axis++)
            {
                // Z is the trigger height rather than an offset, which is why it is written as Z and
                // the others as their own letters
                if (model.Move.Axes[axis].Letter != 'Z')
                {
                    line.Append(CultureInfo.InvariantCulture,
                                $" {model.Move.Axes[axis].Letter}{probe.Offsets[axis]:F1}");
                }
            }
            line.Append(CultureInfo.InvariantCulture, $" Z{probe.TriggerHeight:F3}");
            builder.AppendLine(line.ToString());
        }
    }

    /// <summary>
    /// The tool offsets
    /// </summary>
    /// <param name="builder">Destination</param>
    /// <param name="always">Whether to write them even where they were not probed</param>
    /// <remarks>
    /// Without P10 only the offsets a tool-setting probe measured are written, because an offset
    /// typed into config.g belongs there. <c>tools[].offsetsProbed</c> is the bitmap that says which
    /// </remarks>
    private void AppendToolOffsets(StringBuilder builder, bool always)
    {
        bool any = false;
        foreach (Tool? tool in model.Tools)
        {
            if (tool is null || (!always && tool.OffsetsProbed == 0))
            {
                continue;
            }

            if (!any)
            {
                builder.AppendLine("; Tool offsets");
                any = true;
            }

            StringBuilder line = new();
            line.Append(CultureInfo.InvariantCulture, $"G10 P{tool.Number}");
            for (int axis = 0; axis < model.Move.Axes.Count && axis < tool.Offsets.Count; axis++)
            {
                if (always || (tool.OffsetsProbed & (1 << axis)) != 0)
                {
                    line.Append(CultureInfo.InvariantCulture,
                                $" {model.Move.Axes[axis].Letter}{tool.Offsets[axis]:F2}");
                }
            }
            builder.AppendLine(line.ToString());
        }
    }

    /// <summary>
    /// The workplace coordinate offsets
    /// </summary>
    /// <remarks>
    /// Written as <c>G10 L2</c>, and numbered from one because that is how G54 to G59.3 are numbered.
    /// A workplace whose offsets are all zero is skipped: it is the machine coordinate system, and
    /// writing it would be writing nothing
    /// </remarks>
    private void AppendWorkplaceOffsets(StringBuilder builder)
    {
        bool any = false;
        int numWorkplaces = 0;
        foreach (Axis axis in model.Move.Axes)
        {
            numWorkplaces = Math.Max(numWorkplaces, axis.WorkplaceOffsets.Count);
        }

        for (int workplace = 0; workplace < numWorkplaces; workplace++)
        {
            StringBuilder line = new();
            bool nonZero = false;
            for (int axis = 0; axis < model.Move.Axes.Count; axis++)
            {
                Axis axisConfig = model.Move.Axes[axis];
                float offset = workplace < axisConfig.WorkplaceOffsets.Count
                               ? axisConfig.WorkplaceOffsets[workplace]
                               : 0.0f;
                line.Append(CultureInfo.InvariantCulture, $" {axisConfig.Letter}{offset:F2}");
                nonZero |= offset != 0.0f;
            }

            if (nonZero)
            {
                if (!any)
                {
                    builder.AppendLine("; Workplace coordinates");
                    any = true;
                }
                builder.AppendLine(CultureInfo.InvariantCulture, $"G10 L2 P{workplace + 1}{line}");
            }
        }
    }
}
