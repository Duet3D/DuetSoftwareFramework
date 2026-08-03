using DuetAPI.ObjectModel;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Motion;

/// <summary>
/// The height map currently in effect, and the Z correction it produces
/// </summary>
/// <remarks>
/// <para>
/// Held here rather than in the object model because the object model carries what a client needs to
/// see - which file is loaded, how far it deviates, what grid it covers - while the heights
/// themselves are a few hundred floats that only the move builder reads. RepRapFirmware splits it the
/// same way, which is why <c>heightmap.csv</c> is a file rather than an object model property.
/// </para>
/// <para>
/// A move is built in user coordinates and committed in machine coordinates, so the correction is
/// added on the way down and taken back off on the way up. That is RepRapFirmware's <c>BedTransform</c>
/// and <c>InverseBedTransform</c>
/// </para>
/// </remarks>
/// <param name="model">Object model</param>
public sealed class BedCompensation(Model.ObjectModel model)
{
    /// <summary>The map in effect, or null if bed compensation is off</summary>
    private HeightMap? _map;

    /// <summary>Height above the bed at which the correction has faded to nothing, or zero for no taper</summary>
    private float _taperHeight;

    /// <summary>Whether a height map is loaded and being applied</summary>
    public bool IsActive => _map is not null;

    /// <summary>
    /// Load a height map and start applying it
    /// </summary>
    /// <param name="reader">Source of the file</param>
    /// <param name="fileName">Virtual name of the file, for the object model</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Why the map could not be loaded, or null if it was</returns>
    public async ValueTask<string?> LoadAsync(TextReader reader, string fileName, CancellationToken cancellationToken)
    {
        if (!HeightMap.TryRead(reader, out HeightMap? map, out string? error))
        {
            return error;
        }

        _map = map;
        (float mean, float deviation, _, _) = map!.GetStatistics();

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            MoveCompensation compensation = model.Move.Compensation;
            compensation.File = fileName;
            compensation.Type = MoveCompensationType.Mesh;
            compensation.MeshDeviation ??= new MoveDeviations();
            compensation.MeshDeviation.Mean = mean;
            compensation.MeshDeviation.Deviation = deviation;

            // liveGrid is the grid that was actually probed, which need not be the one M557 currently
            // describes: a map loaded from a file was measured before whatever M557 says now
            ProbeGrid live = compensation.LiveGrid ?? new ProbeGrid();
            for (int i = 0; i < 2; i++)
            {
                live.Axes[i] = map.Axes[i];
                live.Mins[i] = map.Mins[i];
                live.Maxs[i] = map.Maxs[i];
                live.Spacings[i] = map.Spacings[i];
            }
            live.Radius = map.Radius;
            compensation.LiveGrid = live;
        }
        return null;
    }

    /// <summary>
    /// Write the map in effect
    /// </summary>
    /// <param name="writer">Destination</param>
    /// <param name="fileName">Virtual name of the file, for the object model</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Why the map could not be saved, or null if it was</returns>
    public async ValueTask<string?> SaveAsync(TextWriter writer, string fileName, CancellationToken cancellationToken)
    {
        HeightMap? map = _map;
        if (map is null)
        {
            return "No height map loaded";
        }

        map.Write(writer, DateTime.Now);
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            model.Move.Compensation.File = fileName;
        }
        return null;
    }

    /// <summary>
    /// Stop applying bed compensation
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An awaitable task</returns>
    public async ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        _map = null;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            MoveCompensation compensation = model.Move.Compensation;
            compensation.Type = MoveCompensationType.None;
            compensation.File = null;
            compensation.LiveGrid = null;
            compensation.MeshDeviation = null;
        }
    }

    /// <summary>
    /// Set the height above the bed at which the correction fades out
    /// </summary>
    /// <param name="taperHeight">The height in mm, or zero or less for no taper</param>
    public void SetTaperHeight(float taperHeight) => _taperHeight = taperHeight > 0.0f ? taperHeight : 0.0f;

    /// <summary>The taper height in effect, or zero if the correction is not tapered</summary>
    public float TaperHeight => _taperHeight;

    /// <summary>
    /// How much to raise the nozzle at a point, given where it was asked to go
    /// </summary>
    /// <param name="axis0">Coordinate along the first axis of the grid</param>
    /// <param name="axis1">Coordinate along the second</param>
    /// <param name="requestedHeight">Height above the bed the move asked for</param>
    /// <returns>The correction in mm, zero if compensation is off</returns>
    /// <remarks>
    /// Above the taper height the correction is gone entirely, which is what makes a tall print come
    /// out square rather than following the bed all the way up. Below it the correction is scaled by
    /// how far up the taper the move is, which is RepRapFirmware's <c>BedTransform</c>
    /// </remarks>
    public float GetCorrection(float axis0, float axis1, float requestedHeight)
    {
        HeightMap? map = _map;
        if (map is null)
        {
            return 0.0f;
        }

        bool tapering = _taperHeight > 0.0f;
        if (tapering && requestedHeight >= _taperHeight)
        {
            return 0.0f;
        }

        float correction = map.GetInterpolatedHeightError(axis0, axis1);
        if (tapering && correction < _taperHeight)
        {
            correction *= (_taperHeight - requestedHeight) / _taperHeight;
        }
        return correction;
    }

    /// <summary>
    /// Undo <see cref="GetCorrection"/>, recovering the height that was asked for
    /// </summary>
    /// <param name="axis0">Coordinate along the first axis of the grid</param>
    /// <param name="axis1">Coordinate along the second</param>
    /// <param name="machineHeight">Height the machine was commanded to</param>
    /// <returns>The height the move asked for</returns>
    /// <remarks>
    /// The taper makes the correction depend on the height being corrected, so inverting it is not
    /// simply a subtraction. This is RepRapFirmware's <c>InverseBedTransform</c>, solved for the
    /// requested height
    /// </remarks>
    public float GetRequestedHeight(float axis0, float axis1, float machineHeight)
    {
        HeightMap? map = _map;
        if (map is null)
        {
            return machineHeight;
        }

        float correction = map.GetInterpolatedHeightError(axis0, axis1);
        if (_taperHeight <= 0.0f || correction >= _taperHeight)
        {
            return machineHeight - correction;
        }

        float scale = correction / _taperHeight;
        float requested = (machineHeight - (_taperHeight * scale)) / (1.0f - scale);
        return requested < _taperHeight ? requested : machineHeight;
    }
}
