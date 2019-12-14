using DuetAPI.Machine;
using DuetAPI.Utility;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.RemoveHttpEndpoint"/> command
    /// </summary>
    public class RemoveHttpEndpoint : DuetAPI.Commands.RemoveHttpEndpoint
    {
        /// <summary>
        /// Remove a third-party HTTP endpoint
        /// </summary>
        /// <returns>True if the endpint could be removed</returns>
        public override async Task<bool> Execute()
        {
            using (await Model.Provider.AccessReadWriteAsync())
            {
                for (int i = 0; i < Model.Provider.Get.HttpEndpoints.Count; i++)
                {
                    HttpEndpoint ep = Model.Provider.Get.HttpEndpoints[i];
                    if (ep.EndpointType == EndpointType && ep.Namespace == Namespace && ep.Path == Path)
                    {
                        ListHelpers.RemoveItem(Model.Provider.Get.HttpEndpoints, i);
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
