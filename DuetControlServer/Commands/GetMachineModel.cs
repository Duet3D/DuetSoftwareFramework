using System.Threading.Tasks;
using DuetAPI.Machine;

namespace DuetControlServer.Commands
{
    public class GetMachineModel : DuetAPI.Commands.GetMachineModel
    {
        // Return the current machine model
        public override Task<Model> Execute() => Task.FromResult(RepRapFirmware.Model.Current);
    }
}