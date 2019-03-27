using DuetAPI.Machine;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Get the current RepRapFirmware machine model
    /// </summary>
    /// <seealso cref="DuetAPI.Machine.Model"/>
    public class GetMachineModel : Command<Model> { }
}