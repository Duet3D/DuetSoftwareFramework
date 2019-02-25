using System.Threading.Tasks;
using DuetAPI.Machine;

namespace DuetControlServer.Commands
{
    public class GetMachineModel : DuetAPI.Commands.GetMachineModel
    {
        // Return the current machine model. Note that it is not cloned here for performance reasons
        protected override Task<Model> Run() => Task.FromResult(RepRapFirmware.Model.Current);
    }
}