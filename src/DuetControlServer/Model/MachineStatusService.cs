using System;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetControlServer.Files.Job;
using DuetControlServer.Motion;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Model;

/// <summary>
/// Keeps <c>state.status</c> saying what the machine is doing
/// </summary>
/// <remarks>
/// <para>
/// RepRapFirmware has no field for this: <c>GetStatus()</c> works the answer out from the machine's
/// conditions every time it is asked, so the conditions are the only state and the status cannot
/// disagree with them. DuetControlServer's object model is a materialised tree that gets diffed and
/// patched out to clients, so the answer has to be pushed - and pushing it from each of the places
/// that changes a condition is what makes several writers race to describe one machine.
/// </para>
/// <para>
/// So this is the single writer, and everything else sets a condition it reads. That is §14's
/// conclusion applied to <c>state.status</c>: where there are two representations, one is
/// authoritative and the other is a projection. The conditions are authoritative; this is the
/// projection.
/// </para>
/// <para>
/// The order the conditions are tested in is the order RepRapFirmware tests them, and it is not
/// arbitrary. A halted machine is halted whatever else is true of it, and a paused job is paused even
/// though its file is still selected - so the tests run from the most overriding to the least, and
/// the first that matches wins. Within the job states the transitions come before the settled ones,
/// because a job that is pausing is also still processing
/// </para>
/// </remarks>
/// <param name="model">Object model</param>
/// <param name="jobController">What the job is doing, which most of the states come from</param>
/// <param name="planner">Whether anything is still moving</param>
/// <param name="logger">Logger</param>
internal sealed class MachineStatusService(
    ObjectModel model,
    Files.Job.JobController jobController,
    MovePlanner planner,
    ILogger<MachineStatusService> logger) : BackgroundService
{
    /// <summary>
    /// How often the status is re-derived
    /// </summary>
    /// <remarks>
    /// Fast enough that a client sees a button press take effect and slow enough not to churn the
    /// object model's patch stream. The status is what Duet Web Control drives its whole interface
    /// from, so a stale one is visible in a way a stale reading is not
    /// </remarks>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await UpdateAsync(stoppingToken);
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                // A status that cannot be worked out must not stop it being worked out again: the
                // machine keeps running either way, and a service that died here would leave the
                // interface frozen on whatever it last said
                logger.LogError(e, "Failed to update the machine status");
                await Task.Delay(PollInterval, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Work out what the machine is doing and record it
    /// </summary>
    private async ValueTask UpdateAsync(CancellationToken cancellationToken)
    {
        bool moving = planner.IsMoving;

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            MachineStatus status = Derive(moving);
            if (model.State.Status != status)
            {
                model.State.Status = status;
            }
        }
    }

    /// <summary>
    /// The machine's status, from its conditions
    /// </summary>
    /// <param name="moving">Whether the motion engine still has moves to run</param>
    /// <returns>What the machine is doing</returns>
    /// <remarks>The caller must hold the object model lock</remarks>
    private MachineStatus Derive(bool moving)
    {
        // The overriding conditions first, most overriding first. A machine with no link is
        // disconnected whatever it was doing, and a halted one stays halted until it is reset
        if (model.IsDisconnected)
        {
            return MachineStatus.Disconnected;
        }
        if (model.IsUpdating)
        {
            return MachineStatus.Updating;
        }
        if (model.IsHalted)
        {
            return MachineStatus.Halted;
        }
        if (model.IsStarting)
        {
            return MachineStatus.Starting;
        }

        // Then the job, which is one function of its phase: the mapping lives with the phase rather
        // than here, so that a phase added later cannot be left without an answer
        if (jobController.State.Status is MachineStatus jobStatus)
        {
            return jobStatus;
        }

        // TODO ChangingTool is the one remaining transition. A tool change is a macro like any other
        // here, so nothing distinguishes it from the Busy below; it needs the tool subsystem to say
        // that a change is in progress

        // Anything left is the machine working through codes that did not come from a job, which is
        // what a macro or a console command is
        return moving ? MachineStatus.Busy : MachineStatus.Idle;
    }
}
