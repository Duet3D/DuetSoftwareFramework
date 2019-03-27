using System.Threading.Tasks;
using DuetAPI.Machine;
using DuetControlServer.SPI;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the GetMachineModel command
    /// </summary>
    public class GetMachineModel : DuetAPI.Commands.GetMachineModel
    {
        /// <summary>
        /// Retrieve the current machine model
        /// </summary>
        /// <returns>Current machine model</returns>
        protected override Task<Model> Run() => Task.FromResult(ModelProvider.Current);
        #warning This should be safely accessed and cloned...
    }
}