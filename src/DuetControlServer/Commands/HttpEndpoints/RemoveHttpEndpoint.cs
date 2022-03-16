using DuetAPI.ObjectModel;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.RemoveHttpEndpoint"/> command
    /// </summary>
    public sealed class RemoveHttpEndpoint : DuetAPI.Commands.RemoveHttpEndpoint
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Remove a third-party HTTP endpoint
        /// </summary>
        /// <returns>True if the endpoint could be removed</returns>
        public override async Task<bool> Execute()
        {
            using (await Model.Provider.AccessReadWriteAsync())
            {
                for (int i = 0; i < Model.Provider.Get.HttpEndpoints.Count; i++)
                {
                    HttpEndpoint ep = Model.Provider.Get.HttpEndpoints[i];
                    if (ep.EndpointType == EndpointType && ep.Namespace == Namespace && ep.Path == Path)
                    {
                        _logger.Debug("Removed HTTP endpoint {0} machine/{1}/{2}", EndpointType, Namespace, Path);
                        Model.Provider.Get.HttpEndpoints.RemoveAt(i);
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
