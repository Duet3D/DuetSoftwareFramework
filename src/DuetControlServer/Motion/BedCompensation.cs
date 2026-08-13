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

    /// <summary>Constant added to every height the map gives, so that one point of it reads zero</summary>
    /// <remarks>RepRapFirmware's <c>Move::zShift</c> - see <see cref="SetZeroHeightError"/></remarks>
    private float _zShift;

    /// <summary>Whether a height map is loaded and being applied</summary>
    public bool IsActive => _map is not null;

    /// <summary>
    /// Whether the correction still has any effect at the given height
    /// </summary>
    /// <param name="height">Height above the bed, mm</param>
    /// <returns>True if the correction applies</returns>
    /// <remarks>
    /// M376 tapers the correction off with height, on the grounds that a tall print should end up
    /// square even if it starts on a bed that is not flat. Above the taper height there is nothing
    /// left to apply, which is also what decides whether a move has to be segmented to follow the map
    /// </remarks>
    public bool AppliesAt(float height) => IsActive && (_taperHeight <= 0.0f || height < _taperHeight);

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
        _zShift = 0.0f;                         // the shift belonged to the map being replaced
        await PublishAsync(map!, fileName, cancellationToken);
        return null;
    }

    /// <summary>
    /// Say in the object model what is now being applied
    /// </summary>
    /// <param name="map">The map</param>
    /// <param name="fileName">Where it came from, or null if it was just measured</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An awaitable task</returns>
    private async ValueTask PublishAsync(HeightMap map, string? fileName, CancellationToken cancellationToken)
    {
        (float mean, float deviation, _, _) = map.GetStatistics();

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
    }

    /// <summary>
    /// Start applying a map that was just measured
    /// </summary>
    /// <param name="map">The map</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An awaitable task</returns>
    public async ValueTask AdoptAsync(HeightMap map, CancellationToken cancellationToken)
    {
        _map = map;
        _zShift = 0.0f;                         // the shift belonged to the map being replaced
        await PublishAsync(map, fileName: null, cancellationToken);
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

        // As RepRapFirmware's SetIdentityTransform does. The shift normalises a particular map at a
        // particular point, so it means nothing once that map is gone
        _zShift = 0.0f;

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

    /// <summary>
    /// Normalise the map so that it reads zero error at the point just probed
    /// </summary>
    /// <param name="axis0">Coordinate along the first axis of the grid, at the probe</param>
    /// <param name="axis1">Coordinate along its second</param>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>Move::SetZeroHeightError</c>, called by a G30 that redefines the Z origin.
    /// The map says how far the bed deviates from flat, but "flat" is wherever Z was zeroed, and a
    /// G30 has just moved that. Without the shift, a machine that probes at a point where the map
    /// reads -0.1 mm sets Z to the trigger height and is then immediately corrected by that -0.1 mm
    /// at the same point - so the map fights the operation that was supposed to define its datum.
    /// </para>
    /// <para>
    /// The coordinates are the <em>probe's</em>, not the nozzle's, which is why the caller adds the
    /// probe offsets before asking. That is the point the height was actually measured at
    /// </para>
    /// </remarks>
    public void SetZeroHeightError(float axis0, float axis1)
    {
        HeightMap? map = _map;
        _zShift = map is null ? 0.0f : -map.GetInterpolatedHeightError(axis0, axis1);
    }

    /// <summary>
    /// How far the bed deviates at a point, before the taper is applied
    /// </summary>
    /// <param name="map">The map</param>
    /// <param name="axis0">Coordinate along the first axis of the grid</param>
    /// <param name="axis1">Coordinate along its second</param>
    /// <returns>The deviation in mm</returns>
    /// <remarks>
    /// RepRapFirmware's <c>ComputeHeightCorrection</c>, less the averaging over mapped axes that
    /// needs a tool. Both directions of the transform go through it, so the shift cannot be applied
    /// to one and forgotten in the other
    /// </remarks>
    private float ComputeHeightCorrection(HeightMap map, float axis0, float axis1)
        => map.GetInterpolatedHeightError(axis0, axis1) + _zShift;

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

        float correction = ComputeHeightCorrection(map, axis0, axis1);
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

        float correction = ComputeHeightCorrection(map, axis0, axis1);
        if (_taperHeight <= 0.0f || correction >= _taperHeight)
        {
            return machineHeight - correction;
        }

        float scale = correction / _taperHeight;
        float requested = (machineHeight - (_taperHeight * scale)) / (1.0f - scale);
        return requested < _taperHeight ? requested : machineHeight;
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
    internal bool AppliesTo(RawMove move, int numAxes)
    {
        if (!IsActive)
        {
            return false;
        }

        int zAxis = AxisIndices.ZAxisIndex(model.Move);
        return zAxis < 0 || zAxis >= numAxes || AppliesAt(move.Coords[zAxis]);
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
    internal void Apply(RawMove move, int numAxes)
    {
        if (!IsActive)
        {
            return;
        }

        int zAxis = AxisIndices.ZAxisIndex(model.Move);
        if (zAxis < 0 || zAxis >= numAxes)
        {
            return;                             // nothing to correct on a machine with no Z
        }

        (float axis0, float axis1) = GridCoordinates(move.Coords, numAxes);
        move.Coords[zAxis] += GetCorrection(axis0, axis1, move.Coords[zAxis]);
    }

    /// <summary>
    /// Take the bed correction back off a machine position
    /// </summary>
    /// <param name="position">Axis coordinates, corrected on the way in and requested on the way out</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>InverseBedTransform</c>. There is exactly one caller and there should
    /// remain exactly one: the interpreter keeps its own position, so what a move asked for is
    /// already known everywhere except where the machine has ended up somewhere the interpreter did
    /// not put it. That is homing and probing, and it is what <c>SyncInterpreterToMachine</c> is for.
    /// </para>
    /// <para>
    /// It is an approximate inverse rather than an exact one, because the taper makes the correction
    /// depend on the height being corrected - see <see cref="GetRequestedHeight"/>. That is a reason
    /// to invert in as few places as possible, not a reason not to invert here: leaving the
    /// correction in would have the next move compensate an already-compensated Z
    /// </para>
    /// </remarks>
    public void Remove(Span<float> position, int numAxes)
    {
        if (!IsActive)
        {
            return;
        }

        int zAxis = AxisIndices.ZAxisIndex(model.Move);
        if (zAxis < 0 || zAxis >= numAxes)
        {
            return;                             // nothing to correct on a machine with no Z
        }

        (float axis0, float axis1) = GridCoordinates(position, numAxes);
        position[zAxis] = GetRequestedHeight(axis0, axis1, position[zAxis]);
    }

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
    public int MinimumSegments(float deltaAxis0, float deltaAxis1)
    {
        ProbeGrid grid = Grid;
        if (grid.Spacings.Count < 2)
        {
            return 1;
        }

        static int Segments(float distance, float spacing)
            => spacing > 0.0f ? (int)(2.0f * MathF.Abs(distance) / spacing) + 1 : 1;

        return Math.Max(Segments(deltaAxis0, grid.Spacings[0]), Segments(deltaAxis1, grid.Spacings[1]));
    }

    /// <summary>
    /// The height map's two coordinates for a position
    /// </summary>
    /// <param name="coords">Axis coordinates</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <returns>The pair the map is indexed by</returns>
    /// <remarks>
    /// The grid names the two axes it was measured over, so this is a lookup by letter rather than
    /// the first two axes: a machine that probes over U and Y indexes the map by those
    /// </remarks>
    public (float Axis0, float Axis1) GridCoordinates(ReadOnlySpan<float> coords, int numAxes)
    {
        ProbeGrid grid = Grid;
        float[] coordinates = [0.0f, 0.0f];

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
    /// The grid the correction is indexed by: the one being probed if a probe is in progress, else
    /// the one that was probed
    /// </summary>
    private ProbeGrid Grid => model.Move.Compensation.LiveGrid ?? model.Move.Compensation.ProbeGrid;
}
