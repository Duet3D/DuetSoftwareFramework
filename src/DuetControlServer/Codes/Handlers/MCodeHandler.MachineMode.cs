using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// The M-codes that say what kind of machine this is
/// </summary>
/// <remarks>
/// <para>
/// The mode is not a label. It changes what several codes mean: <c>G0</c> is a rapid at the machine's
/// maximum in CNC and laser mode but honours F in FFF mode, and <c>M3</c> starts a spindle in CNC mode
/// where in laser mode it sets the beam power. RepRapFirmware branches on <c>machineType</c> in both
/// places, and until now there was no mode here to branch on.
/// </para>
/// <para>
/// Changing it waits for standstill. The moves already queued were planned under the old mode - a G0
/// among them was given a feed rate by the rule that was in force when it was built - so letting the
/// mode change under them would mean the queue held moves planned two different ways
/// </para>
/// </remarks>
internal partial class MCodeHandler
{
    /// <summary>
    /// M450: report the machine mode
    /// </summary>
    private async ValueTask<Message> HandleReportMachineModeAsync(CancellationToken cancellationToken)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            // RepRapFirmware's wording, which PanelDue and DWC parse
            string mode = model.State.MachineMode switch
            {
                MachineMode.FFF => "FFF",
                MachineMode.CNC => "CNC",
                MachineMode.Laser => "Laser",
                _ => model.State.MachineMode.ToString()
            };
            return new Message(MessageType.Success, $"PrinterMode:{mode}");
        }
    }

    /// <summary>
    /// M451, M452 and M453: select FFF, laser or CNC mode
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="mode">Mode to select</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleSetMachineModeAsync(Commands.Code code, MachineMode mode,
                                                               CancellationToken cancellationToken)
    {
        bool changed;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            changed = model.State.MachineMode != mode;
            model.State.MachineMode = mode;
        }

        if (mode == MachineMode.Laser)
        {
            // TODO M452's own parameters configure the laser: C the port, F or Q its PWM frequency,
            // R the power that counts as full and S whether power persists between moves. None of
            // them has anywhere to go until a laser subsystem exists - state.laser is not in the
            // object model, RawMove has no laser power field, and the wire format has no slot for
            // one. Switching the mode is what M452 can do here, and it is what G0 and M3 need
            if (code.HasParameter('C') || code.HasParameter('R') || code.HasParameter('S')
                || code.HasParameter('F') || code.HasParameter('Q'))
            {
                return new Message(MessageType.Warning,
                    "Laser mode selected, but M452's laser parameters are not supported yet");
            }
        }

        // M453 may be repeated to set up several spindles, so a mode that did not change is not an
        // error - RepRapFirmware only reports the switch on the first one
        _ = changed;
        return new Message();
    }
}
