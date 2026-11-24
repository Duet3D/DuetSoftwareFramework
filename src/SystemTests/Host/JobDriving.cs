using System.Text;
using System.Threading.Tasks;

namespace SystemTests.Host;

/// <summary>Helpers for scenarios that run a job and pause, resume or cancel it</summary>
internal static class JobDriving
{
    /// <summary>
    /// A block of position-neutral zigzag moves. A job completes as soon as its last code is queued,
    /// so a scenario that pauses from the console keeps the job alive by carrying more moves than the
    /// ring holds (40 by default); these fillers are that padding. Each pair nets to nothing, so they
    /// change neither the final position nor any step total
    /// </summary>
    /// <param name="pairs">Number of out-and-back pairs; 30 pairs comfortably exceed the ring</param>
    /// <param name="feed">Feed rate in mm/min, trading how long the padding takes to execute</param>
    public static string FillerMoves(int pairs = 30, int feed = 6000)
    {
        StringBuilder moves = new("G91\n");
        for (int i = 0; i < pairs; i++)
        {
            moves.Append($"G1 Y0.5 F{feed}\nG1 Y-0.5 F{feed}\n");
        }
        return moves.Append("G90\n").ToString();
    }

    /// <summary>
    /// Run the machine until an axis reaches the given position, and stop the timeline there. This is
    /// what a pause scenario names instead of a delay: the stop then lands at a position the test
    /// chose rather than wherever the host happened to be when a timer expired
    /// </summary>
    public static Task RunToPositionAsync(this Bench bench, SteppedTimeline timeline, int axis, double position)
        => timeline.RunUntilAsync(async () => await bench.Host.MachinePositionAsync(axis) >= position,
                                  $"axis {axis} reaching {position}");
}
