using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Tools;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// The M-codes that define and configure a tool
/// </summary>
/// <remarks>
/// A tool is what a coordinate is measured to, so the codes here change the transform every later
/// move goes through. That is why they wait for standstill: an offset changing under a queued move
/// would mean the move was planned against one nozzle and executed with another
/// </remarks>
internal partial class MCodeHandler
{
    /// <summary>
    /// M563: define or delete a tool
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// RepRapFirmware's <c>ManageTool</c>. Redefining a tool replaces it, so a config.g that is re-run
    /// does not accumulate duplicates
    /// </remarks>
    private async ValueTask<Message> HandleDefineToolAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!code.TryGetInt('P', out int toolNumber))
        {
            return await ReportToolsAsync(cancellationToken);
        }

        // The offsets a tool carries are part of the transform every queued move was planned against,
        // so they cannot change while one is in flight
        if (!await planner.WaitForStandstillAsync(cancellationToken))
        {
            throw new System.OperationCanceledException();
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            List<int> extruders = IntArray(code, 'D');
            ToolDefinition definition = new()
            {
                Number = toolNumber,
                Name = code.TryGetString('S', out string? name) ? name : null,
                Extruders = extruders,
                Heaters = IntArray(code, 'H'),
                Fans = code.HasParameter('F') ? IntArray(code, 'F') : [0],
                XMap = code.HasParameter('X') ? IntArray(code, 'X') : [0],
                YMap = code.HasParameter('Y') ? IntArray(code, 'Y') : [1],
                ZMap = code.HasParameter('Z') ? IntArray(code, 'Z') : [2],

                // RepRapFirmware defaults this to the tool's only drive when it has exactly one:
                // with several drives there is no single filament to speak of
                FilamentExtruder = code.TryGetInt('L', out int filament) ? filament
                                   : extruders.Count == 1 ? extruders[0] : -1,
                Spindle = code.TryGetInt('R', out int spindle) ? spindle : -1
            };

            string? error = toolManager.Define(definition);
            if (error is not null)
            {
                return new Message(MessageType.Error, error);
            }
        }

        return new Message();
    }

    /// <summary>
    /// Report the tools the machine has, as M563 with no parameters does
    /// </summary>
    private async ValueTask<Message> ReportToolsAsync(CancellationToken cancellationToken)
    {
        StringBuilder builder = new();
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            foreach (Tool? tool in model.Tools)
            {
                if (tool is null)
                {
                    continue;
                }

                builder.Append(CultureInfo.InvariantCulture, $"Tool {tool.Number}");
                if (!string.IsNullOrEmpty(tool.Name))
                {
                    builder.Append(CultureInfo.InvariantCulture, $" \"{tool.Name}\"");
                }
                AppendList(builder, " drives", tool.Extruders);
                AppendList(builder, " heaters", tool.Heaters);
                AppendOffsets(builder, tool, model);
                builder.AppendLine();
            }

            if (builder.Length == 0)
            {
                return new Message(MessageType.Success, "No tools are defined");
            }
        }
        return new Message(MessageType.Success, builder.ToString().TrimEnd());
    }

    private static void AppendList(StringBuilder builder, string label, IReadOnlyList<int> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        builder.Append(label).Append(':');
        foreach (int value in values)
        {
            builder.Append(CultureInfo.InvariantCulture, $" {value}");
        }
    }

    private static void AppendOffsets(StringBuilder builder, Tool tool, Model.ObjectModel model)
    {
        bool any = false;
        for (int axis = 0; axis < tool.Offsets.Count && axis < model.Move.Axes.Count; axis++)
        {
            if (tool.Offsets[axis] != 0.0f)
            {
                if (!any)
                {
                    builder.Append(" offsets:");
                    any = true;
                }
                builder.Append(CultureInfo.InvariantCulture,
                               $" {model.Move.Axes[axis].Letter}{tool.Offsets[axis]:F2}");
            }
        }
    }

    /// <summary>
    /// Read an integer array parameter, treating a single value as a one-element array
    /// </summary>
    /// <remarks>
    /// A negative entry means "none of them", which is how <c>M563 P0 F-1</c> gives a tool no fans.
    /// RepRapFirmware reads the array into a signed type for exactly that reason
    /// </remarks>
    private static List<int> IntArray(Commands.Code code, char letter)
    {
        List<int> values = [];
        if (code.TryGetIntArray(letter, out int[]? array))
        {
            foreach (int value in array)
            {
                if (value >= 0)
                {
                    values.Add(value);
                }
            }
        }
        return values;
    }
}
