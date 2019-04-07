using DuetAPI.Machine;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Get the current RepRapFirmware machine model
    /// </summary>
    /// <seealso cref="DuetAPI.Machine.MachineModel"/>
    public class GetMachineModel : Command<MachineModel> { }
}