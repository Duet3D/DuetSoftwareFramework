using DuetAPI.ObjectModel;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.Command"/> command
    /// </summary>
    public class GetMachineModel : DuetAPI.Commands.GetObjectModel
    {
        /// <summary>
        /// Retrieve a copy of the current machine model
        /// </summary>
        /// <returns>Clone of the current machine model</returns>
        public override async Task<ObjectModel> Execute()
        {
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                return (ObjectModel)Model.Provider.Get.Clone();
            }
        }
    }
}