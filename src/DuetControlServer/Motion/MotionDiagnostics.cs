using DuetControlServer.Link;
using DuetControlServer.Link.Native;
using DuetControlServer.Utility;
using System.Globalization;
using System.Text;

namespace DuetControlServer.Motion;

/// <summary>
/// The motion engine's contribution to M122
/// </summary>
/// <remarks>
/// <para>
/// The engine keeps these counters and cannot format them: it runs on a real-time thread that must
/// not build strings, and the wording of a reply belongs on this side in any case. So the native
/// side reports numbers through <c>DuetSbc_MotionGetStats</c> and this renders them.
/// </para>
/// <para>
/// The shape follows RepRapFirmware's <c>Move::Diagnostics</c> - an <c>=== Move ===</c> block and one
/// <c>=== DDARing n ===</c> per ring - so that an M122 from this machine reads like an M122 from a
/// Duet. The counters that have no RRF equivalent are the ones that only exist because the engine is
/// on the far side of a link: dropped submissions, dropped ScheduleMove packets, forced positions.
/// </para>
/// <para>
/// Reading and resetting are separate calls. The native side used to report and zero in one step, so
/// a second M122 showed zeros however bad the first had been.
/// </para>
/// </remarks>
/// <param name="linkInterface">Link to the native engine</param>
[DiagnosticsPriority(-4)]
public sealed class MotionDiagnostics(LinkInterface linkInterface) : IDiagnostics
{
    /// <summary>
    /// Print the motion engine's diagnostics
    /// </summary>
    /// <param name="builder">String builder to print to</param>
    public void PrintDiagnostics(StringBuilder builder)
    {
        NativeMotionStats stats = linkInterface.Native.GetMotionStats();

        builder.AppendLine("=== Move ===");
        builder.AppendLine(string.Format(CultureInfo.InvariantCulture,
                                         "Segments created {0}, movement delay {1:F1}ms",
                                         stats.SegmentsCreated,
                                         stats.MovementDelayTicks * 1000.0 / Native.MotionLimits.StepClockRate));

        // Any of these being non-zero means work was lost rather than delayed, so they are worth
        // saying plainly rather than folding into the per-ring line
        if (stats.DroppedSchedulePackets > 0 || stats.SubmissionsDropped > 0)
        {
            builder.AppendLine(string.Format(CultureInfo.InvariantCulture,
                                             "MOTION LOST: {0} submissions refused, {1} schedule packets dropped",
                                             stats.SubmissionsDropped, stats.DroppedSchedulePackets));
        }
        builder.AppendLine($"Forced positions applied: {stats.ForcedPositionsApplied}");

        for (int i = 0; i < NativeMotionStats.MaxRings; i++)
        {
            NativeRingStats ring = stats.Rings[i];
            builder.AppendLine($"=== DDARing {i} ===");
            builder.AppendLine($"Scheduled moves {ring.ScheduledMoves}, completed {ring.CompletedMoves}, "
                               + $"LaErrors {ring.NumLookaheadErrors}, "
                               + $"Underruns [{ring.NumLookaheadUnderruns}, {ring.NumNoMoveUnderruns}]");
        }

        // Cleared only once they have been reported, so nothing is lost between the read and the reset
        linkInterface.Native.ResetMotionStats();
    }
}
