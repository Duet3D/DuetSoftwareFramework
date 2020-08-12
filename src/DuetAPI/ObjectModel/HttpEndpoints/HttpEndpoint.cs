namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Class representing an extra HTTP endpoint
    /// </summary>
    /// <seealso cref="Commands.AddHttpEndpoint"/>
    /// <seealso cref="Commands.RemoveHttpEndpoint"/>
    public sealed class HttpEndpoint : ModelObject
    {
        /// <summary>
        /// HTTP type of this endpoint
        /// </summary>
        public HttpEndpointType EndpointType
        {
            get => _endpointType;
			set => SetPropertyValue(ref _endpointType, value);
        }
        private HttpEndpointType _endpointType;

        /// <summary>
        /// Namespace of the endpoint
        /// </summary>
        public string Namespace
        {
            get => _namespace;
			set => SetPropertyValue(ref _namespace, value);
        }
        private string _namespace;

        /// <summary>
        /// Path to the endpoint
        /// </summary>
        public string Path
        {
            get => _path;
			set => SetPropertyValue(ref _path, value);
        }
        private string _path;

        /// <summary>
        /// Path to the UNIX socket
        /// </summary>
        public string UnixSocket
        {
            get => _unixSocket;
			set => SetPropertyValue(ref _unixSocket, value);
        }
        private string _unixSocket;
    }
}
