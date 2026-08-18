using System.Collections.Generic;

namespace DuetControlServer.Motion;

/// <summary>
/// What a queued move came from in the job file
/// </summary>
/// <remarks>
/// The motion engine knows a move by its id and nothing else about it: <c>MoveParams</c> carries no
/// file position, and it should not - the engine has no idea what a file is. So a feedhold reports
/// the id of the first move it dropped and this is what turns that back into somewhere to resume
/// from. <c>EndstopCorrection.NoteMoveId</c> is the same arrangement for the same reason
/// </remarks>
internal readonly record struct JobMoveOrigin
{
    /// <summary>
    /// Where in the job file the code that produced the move started
    /// </summary>
    public long? FilePosition { get; init; }

    /// <summary>
    /// Which of G0/G1/G2/G3 produced it, or -1 if not known
    /// </summary>
    public int GCommandNumber { get; init; }

    /// <summary>
    /// Feed rate the move was asked for, in mm/s
    /// </summary>
    public float FeedRateMmPerSec { get; init; }
}

/// <summary>
/// Remembers where each queued job move came from, so a feedhold can say where to resume
/// </summary>
/// <remarks>
/// <para>
/// Bounded on purpose. A job queues moves far faster than they run, so an index that only forgot
/// what completed would still grow without limit whenever the ring is full and the engine is behind.
/// The oldest entry is dropped once the index is full, which is safe because a feedhold can only
/// ever name a move that is still queued.
/// </para>
/// <para>
/// Not thread-safe: the planner lock covers it, as it covers everything else the planner queues
/// </para>
/// </remarks>
internal sealed class JobMoveIndex
{
    /// <summary>
    /// How many moves are remembered
    /// </summary>
    /// <remarks>
    /// Comfortably more than a ring holds, so the entry a feedhold asks about is always still here.
    /// The ring is what bounds how many moves can be queued at once, and it is far smaller
    /// </remarks>
    private const int Capacity = 512;

    private readonly Dictionary<uint, JobMoveOrigin> _origins = new(Capacity);
    private readonly Queue<uint> _order = new(Capacity);

    /// <summary>
    /// Record where a move came from
    /// </summary>
    /// <param name="moveId">Id the move was queued under</param>
    /// <param name="origin">Where it came from</param>
    public void Note(uint moveId, JobMoveOrigin origin)
    {
        if (moveId == 0)
        {
            return;             // zero means "no id" on the wire, so there is nothing to key on
        }

        if (_origins.TryAdd(moveId, origin))
        {
            _order.Enqueue(moveId);
        }
        else
        {
            _origins[moveId] = origin;
        }

        while (_order.Count > Capacity && _order.TryDequeue(out uint oldest))
        {
            _origins.Remove(oldest);
        }
    }

    /// <summary>
    /// Look up where a move came from
    /// </summary>
    /// <param name="moveId">Id the move was queued under</param>
    /// <param name="origin">Receives where it came from</param>
    /// <returns>True if the move is still remembered</returns>
    public bool TryGet(uint moveId, out JobMoveOrigin origin) => _origins.TryGetValue(moveId, out origin);

    /// <summary>
    /// Forget everything, for when the moves it describes are gone
    /// </summary>
    public void Clear()
    {
        _origins.Clear();
        _order.Clear();
    }
}
