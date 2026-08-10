using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// The bed compensation M-codes, ported from RepRapFirmware's <c>GCodes::HandleMcode</c>
/// </summary>
/// <remarks>
/// The grid is configuration and lives in <c>move.compensation.probeGrid</c>; the map measured over
/// that grid is data and lives in a file, which is also where RepRapFirmware keeps it. What is loaded
/// is published as <c>move.compensation.liveGrid</c>, so the object model says both what would be
/// probed and what is currently in effect
/// </remarks>
internal partial class MCodeHandler
{
    /// <summary>Smallest point spacing a grid may have, as in RepRapFirmware's <c>GridDefinition::MinSpacing</c></summary>
    private const float MinGridSpacing = 0.1f;

    /// <summary>Smallest range an axis of a grid may span, as in RepRapFirmware's <c>GridDefinition::MinRange</c></summary>
    private const float MinGridRange = 1.0f;

    /// <summary>Most points one row of a grid may have, as in RepRapFirmware's <c>MaxAxis0GridPoints</c></summary>
    private const int MaxGridPointsPerRow = 41;

    /// <summary>Most points a grid may have in total, as in RepRapFirmware's <c>MaxGridProbePoints</c></summary>
    private const int MaxGridPoints = 441;

    /// <summary>Grid spacing used when M557 names a range but no spacing, as in RepRapFirmware's <c>DefaultGridSpacing</c></summary>
    private const float DefaultGridSpacing = 20.0f;

    /// <summary>
    /// M557: define the grid that mesh bed compensation probes
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// Either two axis ranges or a radius, and either a point count or a spacing. RepRapFirmware
    /// derives whichever of the two was not given, so that a grid can be described the way that suits
    /// the machine - a rectangular bed by its corners, a delta by its radius
    /// </remarks>
    private async ValueTask<Message> HandleProbeGridAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
        {
            throw new OperationCanceledException();
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            char[] letters = ['X', 'Y'];
            float[][] ranges = [new float[2], new float[2]];
            float[] spacings = [DefaultGridSpacing, DefaultGridSpacing];

            int axesSeen = 0;
            for (int axis = 0; axis < model.Move.Axes.Count; axis++)
            {
                char letter = model.Move.Axes[axis].Letter;
                if (!code.TryGetFloatArray(letter, out float[]? values))
                {
                    continue;
                }

                if (letter == 'Z')
                {
                    return new Message(MessageType.Error, "Z axis is not allowed for mesh leveling");
                }
                if (axesSeen == 2)
                {
                    return new Message(MessageType.Error, "Mesh leveling expects exactly two axes");
                }
                if (values.Length < 2)
                {
                    return new Message(MessageType.Error, $"Expected a minimum and a maximum for {letter}");
                }

                letters[axesSeen] = letter;
                ranges[axesSeen][0] = values[0];
                ranges[axesSeen][1] = values[1];
                axesSeen++;
            }

            if (axesSeen == 1)
            {
                return new Message(MessageType.Error, "Specify zero or two axes in M557");
            }

            bool seenPoints = code.TryGetIntArray('P', out int[]? numPoints);
            bool seenSpacing = false;
            if (!seenPoints && code.TryGetFloatArray('S', out float[]? given) && given.Length > 0)
            {
                spacings[0] = given[0];
                spacings[1] = given.Length > 1 ? given[1] : given[0];
                seenSpacing = true;
            }

            bool seenRadius = code.TryGetFloat('R', out float radius);
            if (!seenRadius)
            {
                radius = -1.0f;
            }

            if (axesSeen == 0 && !seenRadius && !seenSpacing && !seenPoints)
            {
                return new Message(MessageType.Success, DescribeProbeGrid(model.Move.Compensation.ProbeGrid));
            }

            if (axesSeen == 0 && !seenRadius)
            {
                return new Message(MessageType.Error, "Specify at least a radius or two axis ranges in M557");
            }

            if (seenPoints && (numPoints!.Length == 0 || numPoints[0] < 2))
            {
                return new Message(MessageType.Error, "Expected at least two points per axis");
            }

            int[] points = seenPoints
                ? [numPoints![0], numPoints.Length > 1 ? numPoints[1] : numPoints[0]]
                : [0, 0];

            if (axesSeen > 0)
            {
                if (seenPoints)
                {
                    // Nudged just below the exact spacing so that dividing the range by it gives the
                    // point count that was asked for rather than occasionally one less
                    for (int i = 0; i < 2; i++)
                    {
                        if (points[i] >= 2 && ranges[i][1] > ranges[i][0])
                        {
                            spacings[i] = (ranges[i][1] - ranges[i][0]) / (points[i] - 1) * 0.9999f;
                        }
                    }
                }
            }
            else if (radius > 0.0f)
            {
                for (int i = 0; i < 2; i++)
                {
                    // The grid is inscribed in the circle, so a row that straddles the centre reaches
                    // further out than one that sits either side of it
                    float effective;
                    if (seenPoints && points[i] >= 2)
                    {
                        effective = radius - 0.1f;
                        int otherPoints = points[1 - i];
                        if (otherPoints % 2 == 0)
                        {
                            effective *= MathF.Sqrt(1.0f - (1.0f / ((otherPoints - 1) * (otherPoints - 1))));
                        }
                        spacings[i] = 2 * effective / (points[i] - 1);
                    }
                    else
                    {
                        effective = MathF.Floor((radius - 0.1f) / spacings[i]) * spacings[i];
                    }
                    ranges[i][0] = -effective;
                    ranges[i][1] = effective + 0.1f;
                }
            }
            else
            {
                return new Message(MessageType.Error, "M557 radius must be positive unless two axis ranges are specified");
            }

            // Y is found before U however the command was written, so an IDEX machine given U and Y
            // would end up with them the wrong way round. RepRapFirmware swaps them for the same reason
            if (letters[0] == 'Y' && letters[1] == 'U')
            {
                (letters[0], letters[1]) = (letters[1], letters[0]);
                (ranges[0], ranges[1]) = (ranges[1], ranges[0]);
                (spacings[0], spacings[1]) = (spacings[1], spacings[0]);
            }

            if (GridRefusal(ranges, spacings) is string error)
            {
                return new Message(MessageType.Error, $"Bad grid definition: {error}");
            }

            ProbeGrid grid = model.Move.Compensation.ProbeGrid;
            for (int i = 0; i < 2; i++)
            {
                grid.Axes[i] = letters[i];
                grid.Mins[i] = ranges[i][0];
                grid.Maxs[i] = ranges[i][1];
                grid.Spacings[i] = spacings[i];
            }
            grid.Radius = radius;
        }
        return new Message();
    }

    /// <summary>
    /// Why a grid cannot be probed, or null if it can
    /// </summary>
    /// <param name="ranges">Minimum and maximum of each axis</param>
    /// <param name="spacings">Point spacing along each axis</param>
    /// <returns>The reason</returns>
    private static string? GridRefusal(float[][] ranges, float[] spacings)
    {
        if (spacings[0] < MinGridSpacing || spacings[1] < MinGridSpacing)
        {
            return "spacing too small";
        }

        for (int i = 0; i < 2; i++)
        {
            if (ranges[i][1] - ranges[i][0] < MinGridRange)
            {
                return "axis range too small";
            }
        }

        int alongFirst = PointsAlong(ranges[0], spacings[0]);
        int alongSecond = PointsAlong(ranges[1], spacings[1]);
        if (alongFirst > MaxGridPointsPerRow || alongFirst * alongSecond > MaxGridPoints)
        {
            return $"too many grid points, at most {MaxGridPointsPerRow} per row and {MaxGridPoints} in total";
        }
        return null;
    }

    /// <summary>
    /// How many points a grid has along one axis
    /// </summary>
    /// <param name="range">Minimum and maximum</param>
    /// <param name="spacing">Point spacing</param>
    /// <returns>The count</returns>
    private static int PointsAlong(float[] range, float spacing)
        => (int)MathF.Floor((range[1] - range[0]) / spacing) + 1;

    /// <summary>
    /// Describe a grid the way M557 with no parameters does
    /// </summary>
    /// <param name="grid">The grid</param>
    /// <returns>The description</returns>
    private static string DescribeProbeGrid(ProbeGrid grid)
    {
        if (grid.Maxs[0] - grid.Mins[0] < MinGridRange || grid.Spacings[0] < MinGridSpacing)
        {
            return "Grid is not defined";
        }

        int points = PointsAlong([grid.Mins[0], grid.Maxs[0]], grid.Spacings[0])
                     * PointsAlong([grid.Mins[1], grid.Maxs[1]], grid.Spacings[1]);
        StringBuilder builder = new("Grid: ");
        builder.Append(CultureInfo.InvariantCulture, $"{grid.Axes[0]}{grid.Mins[0]:F1}:{grid.Maxs[0]:F1}");
        builder.Append(CultureInfo.InvariantCulture, $", {grid.Axes[1]}{grid.Mins[1]:F1}:{grid.Maxs[1]:F1}");
        builder.Append(CultureInfo.InvariantCulture, $", radius {grid.Radius:F1}");
        builder.Append(CultureInfo.InvariantCulture, $", {grid.Axes[0]} spacing {grid.Spacings[0]:F1}");
        builder.Append(CultureInfo.InvariantCulture, $", {grid.Axes[1]} spacing {grid.Spacings[1]:F1}");
        builder.Append(CultureInfo.InvariantCulture, $", {points} points");
        return builder.ToString();
    }

    /// <summary>
    /// Default height map file, as in RepRapFirmware's <c>DefaultHeightMapFile</c>
    /// </summary>
    private const string DefaultHeightMapFile = "heightmap.csv";

    /// <summary>
    /// M374: save the height map in effect to a file
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleSaveHeightMapAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        string fileName = code.GetString('P', DefaultHeightMapFile);
        string physicalFile = await filePathResolver.ToPhysicalAsync(fileName, FileDirectory.System, cancellationToken);

        await using StreamWriter writer = new(physicalFile);
        if (await bedCompensation.SaveAsync(writer, fileName, cancellationToken) is string error)
        {
            return new Message(MessageType.Error, error);
        }
        return new Message(MessageType.Success, $"Height map saved to file {fileName}");
    }

    /// <summary>
    /// M375: load a height map from a file and enable bed compensation
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// The map replaces whatever was in effect, so the old one is dropped first: a load that fails
    /// part way through must not leave half of one map and half of another being applied
    /// </remarks>
    private async ValueTask<Message> HandleLoadHeightMapAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!await FlushAndWaitForStandstillAsync(code, cancellationToken))
        {
            throw new OperationCanceledException();
        }

        await bedCompensation.ClearAsync(cancellationToken);

        string fileName = code.GetString('P', DefaultHeightMapFile);
        string physicalFile = await filePathResolver.ToPhysicalAsync(fileName, FileDirectory.System, cancellationToken);
        if (!File.Exists(physicalFile))
        {
            return new Message(MessageType.Error, $"Height map file {fileName} not found");
        }

        using StreamReader reader = new(physicalFile);
        if (await bedCompensation.LoadAsync(reader, fileName, cancellationToken) is string error)
        {
            return new Message(MessageType.Error, $"Failed to load height map from file {fileName}: {error}");
        }
        return new Message(MessageType.Success, $"Height map loaded from file {fileName}");
    }

    /// <summary>
    /// M376: set or report the height above the bed at which compensation fades out
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleTaperHeightAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!code.TryGetFloat('H', out float taperHeight))
        {
            float current = bedCompensation.TaperHeight;
            return new Message(MessageType.Success, current > 0.0f
                ? string.Create(CultureInfo.InvariantCulture, $"Bed compensation taper height is {current:F1}mm")
                : "Bed compensation is not tapered");
        }

        bedCompensation.SetTaperHeight(taperHeight);
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            // Reported so a client can show the taper without asking for it, which is how
            // RepRapFirmware reports it too
            model.Move.Compensation.FadeHeight = taperHeight > 0.0f ? taperHeight : null;
        }
        return new Message();
    }

    /// <summary>
    /// M561: stop applying bed compensation
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// Waits for standstill first: the correction is applied when a move is built, so dropping it
    /// while moves are queued would leave the machine partway between two coordinate systems
    /// </remarks>
    private async ValueTask<Message> HandleClearCompensationAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!await FlushAndWaitForStandstillAsync(code, cancellationToken))
        {
            throw new OperationCanceledException();
        }

        await bedCompensation.ClearAsync(cancellationToken);
        return new Message();
    }
}
