using DuetAPI.ObjectModel;
using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Remove an existing HTTP endpoint.
    /// Returns true if the endpoint could be successfully removed
    /// </summary>
    [RequiredPermissions(SbcPermissions.RegisterHttpEndpoints)]
    public class RemoveHttpEndpoint : Command<bool>
    {
        /// <summary>
        /// Type of the endpoint
        /// </summary>
        public HttpEndpointType EndpointType { get; set; }

        /// <summary>
        /// Namespace of the endpoint
        /// </summary>
        public string Namespace { get; set; }

        /// <summary>
        /// Endpoint path to unregister
        /// </summary>
        public string Path { get; set; }
    }
}
