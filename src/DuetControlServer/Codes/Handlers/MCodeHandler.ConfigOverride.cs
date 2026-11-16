using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPI.Commands;
using DuetControlServer.Files;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Model;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// M500: write the settings a machine calibrates for itself into config-override.g
/// </summary>
/// <remarks>
/// RepRapFirmware's <c>GCodes::WriteConfigOverrideFile</c>, whose sections and their order this
/// follows. M501 runs the file, so every line is a comment or a code
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
    private enum ConfigOverrideSaveOption : int
    {
        SaveToolOffsets = 10,
        SaveProbeValues = 31,
        SaveCoordinateRotation = 68,
    }


    /// <summary>
    /// Opt-ins M500 P may name at once
    /// </summary>
    private const int MaxSaveOptions = 3;

    /// <summary>
    /// M500: save the calibrated settings
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleSaveConfigOverrideAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        bool saveToolOffsets = false;
        bool saveProbeValues = false;
        bool saveRotation = false;
        if (code.TryGetEnumArray('P', MaxSaveOptions, out ConfigOverrideSaveOption[]? requested, ignoreInvalid: true)) // Invalid values ignored as per RRF
        {
            foreach (ConfigOverrideSaveOption value in requested)
            {
                saveToolOffsets |= value == ConfigOverrideSaveOption.SaveToolOffsets;
                saveProbeValues |= value == ConfigOverrideSaveOption.SaveProbeValues;
                saveRotation |= value == ConfigOverrideSaveOption.SaveCoordinateRotation;
            }
        }

        StringBuilder builder = new();
        builder.AppendLine(CultureInfo.InvariantCulture,
                           $"; config-override.g file generated in response to M500 at {DateTime.Now:yyyy-MM-dd HH:mm}");
        builder.AppendLine("; This is a system-generated file - do not edit");

        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            using (planner.Lock())
            {
                planner.Parameters.Geometry.AppendCalibration(builder);
            }
            AppendHeaterModels(builder);
            AppendProbedAxisLimits(builder);
            AppendProbeValues(builder, saveProbeValues);
            AppendToolOffsets(builder, saveToolOffsets);
            AppendWorkplaceOffsets(builder);
            if (saveRotation)
            {
                AppendCoordinateRotation(builder);
            }
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

            // A file cut short would load half the settings, as RepRapFirmware's DeleteSysFile prevents
            try
            {
                if (File.Exists(physicalPath))
                {
                    File.Delete(physicalPath);
                }
            }
            catch (Exception deleteError)
            {
                logger.LogDebug(deleteError, "Could not delete the incomplete {File}", FilePathResolver.ConfigOverrideFile);
            }
            return new Message(MessageType.Error,
                               $"Failed to write file {FilePathResolver.ConfigOverrideFile}: {e.Message}");
        }

        return configOverride.LoadedByConfig
               ? new Message()
               : new Message(MessageType.Warning, "No M501 command was executed in config.g");
    }

    /// <summary>
    /// The heater models, which is what M303 tuning produced
    /// </summary>
    /// <remarks>RepRapFirmware's <c>Heat::WriteModelParameters</c>, which writes enabled models only</remarks>
    private void AppendHeaterModels(StringBuilder builder)
    {
        builder.AppendLine("; Heater model parameters");
        for (int heaterNumber = 0; heaterNumber < model.Heat.Heaters.Count; heaterNumber++)
        {
            if (model.Heat.Heaters[heaterNumber] is Heater { Model.Enabled: true } heater)
            {
                HeaterFunction function = heatManager.GetHeaterFunction(heaterNumber);
                Heat.FopDt.AppendM307Command(builder, Heat.FopDt.ToCanMessage(heaterNumber, function, heater.Model),
                                             function == HeaterFunction.Tool);
            }
        }
    }

    /// <summary>
    /// Axis limits that were measured rather than configured
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>Move::WriteMoveParameters</c>: only the limits marked probed, which a
    /// <c>G1 H3</c> or config-override.g set. A limit config.g set is already in config.g
    /// </remarks>
    private void AppendProbedAxisLimits(StringBuilder builder)
    {
        bool anyProbed = false;
        foreach (Axis axis in model.Move.Axes)
        {
            anyProbed |= axis.MinProbed || axis.MaxProbed;
        }

        if (anyProbed)
        {
            builder.AppendLine("; Probed axis limits");
            AppendAxisLimits(builder, minima: true);
            AppendAxisLimits(builder, minima: false);
        }
    }

    private void AppendAxisLimits(StringBuilder builder, bool minima)
    {
        StringBuilder line = new();
        foreach (Axis axis in model.Move.Axes)
        {
            if (minima ? axis.MinProbed : axis.MaxProbed)
            {
                line.Append(CultureInfo.InvariantCulture, $" {axis.Letter}{(minima ? axis.Min : axis.Max):F2}");
            }
        }

        if (line.Length > 0)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"M208 S{(minima ? 1 : 0)}{line}");
        }
    }

    /// <summary>
    /// The Z probe trigger heights and offsets
    /// </summary>
    /// <param name="builder">Destination</param>
    /// <param name="p31">Whether M500 was given P31, which marks every probe as saved from now on</param>
    /// <remarks>
    /// RepRapFirmware's <c>EndstopsManager::WriteZProbeParameters</c> and
    /// <c>ZProbe::WriteParameters</c>. A probe is written once P31 or config-override.g has marked it
    /// </remarks>
    private void AppendProbeValues(StringBuilder builder, bool p31)
    {
        bool any = false;
        for (int probeNumber = 0; probeNumber < model.Sensors.Probes.Count; probeNumber++)
        {
            if (model.Sensors.Probes[probeNumber] is not Probe probe)
            {
                continue;
            }
            if (p31)
            {
                configOverride.SaveProbe(probe);
            }
            if (!configOverride.IsProbeSaved(probe))
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
            line.Append(CultureInfo.InvariantCulture, $" Z{probe.TriggerHeight:F2}");
            builder.AppendLine(line.ToString());
        }
    }

    /// <summary>
    /// The tool offsets
    /// </summary>
    /// <param name="builder">Destination</param>
    /// <param name="always">Whether to write every offset of every tool, which is what P10 asks for</param>
    /// <remarks>
    /// RepRapFirmware's <c>GCodes::WriteToolParameters</c>. Without P10 only the axes in
    /// <c>tools[].offsetsProbed</c> are written, because an offset typed into config.g belongs there
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
                builder.AppendLine("; Probed tool offsets");
                any = true;
            }

            StringBuilder line = new();
            line.Append(CultureInfo.InvariantCulture, $"G10 P{tool.Number}");
            for (int axis = 0; axis < model.Move.Axes.Count; axis++)
            {
                Axis axisConfig = model.Move.Axes[axis];
                if (axisConfig.Visible && (always || (tool.OffsetsProbed & (1 << axis)) != 0))
                {
                    float offset = axis < tool.Offsets.Count ? tool.Offsets[axis] : 0.0f;
                    line.Append(CultureInfo.InvariantCulture, $" {axisConfig.Letter}{offset:F3}");
                }
            }
            builder.AppendLine(line.ToString());
        }
    }

    /// <summary>
    /// The workplace coordinate offsets
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>GCodes::WriteWorkplaceCoordinates</c>: every workplace on every visible
    /// axis, numbered from one as G10 L2 numbers them. A workplace of zeros is written too, so that
    /// M501 puts it back over whatever config.g set
    /// </remarks>
    private void AppendWorkplaceOffsets(StringBuilder builder)
    {
        builder.AppendLine("; Workplace coordinates");
        for (int workplace = 0; workplace < MachineLimits.NumWorkplaces; workplace++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"G10 L2 P{workplace + 1}");
            foreach (Axis axis in model.Move.Axes)
            {
                if (axis.Visible)
                {
                    float offset = workplace < axis.WorkplaceOffsets.Count ? axis.WorkplaceOffsets[workplace] : 0.0f;
                    builder.Append(CultureInfo.InvariantCulture, $" {axis.Letter}{offset:F2}");
                }
            }
            builder.AppendLine();
        }
    }

    /// <summary>
    /// The G68 rotation of the first motion system, or G69 if there is none
    /// </summary>
    /// <remarks>RepRapFirmware's <c>GCodes::WriteCoordinateRotation</c></remarks>
    private void AppendCoordinateRotation(StringBuilder builder)
    {
        MoveRotation? rotation = model.Move.MotionSystems.Count > 0 ? model.Move.MotionSystems[0].Rotation : null;
        if (rotation is { Angle: not 0.0f })
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                               $"G68 X{rotation.Centre[0]:F3} Y{rotation.Centre[1]:F3} R{rotation.Angle:F2}");
        }
        else
        {
            builder.AppendLine("G69");
        }
    }
}
