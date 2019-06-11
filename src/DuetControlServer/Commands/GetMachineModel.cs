using DuetAPI.Machine;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.GetMachineModel"/> command
    /// </summary>
    public class GetMachineModel : DuetAPI.Commands.GetMachineModel
    {
        /// <summary>
        /// Retrieve a copy of the current machine model
        /// </summary>
        /// <returns>Clone of the current machine model</returns>
        public override async Task<MachineModel> Execute()
        {
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                return (MachineModel)Model.Provider.Get.Clone();
            }
        }
    }
}