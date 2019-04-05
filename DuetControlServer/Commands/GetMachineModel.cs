using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the GetMachineModel command
    /// </summary>
    public class GetMachineModel : DuetAPI.Commands.GetMachineModel
    {
        /// <summary>
        /// Retrieve a copy of the current machine model
        /// </summary>
        /// <returns>Clone of the current machine model</returns>
        protected override async Task<DuetAPI.Machine.Model> Run()
        {
            using (await Model.Provider.AccessReadOnly())
            {
                return (DuetAPI.Machine.Model)Model.Provider.Get.Clone();
            }
        }
    }
}