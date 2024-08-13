namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Class representing an extra HTTP endpoint
    /// </summary>
    /// <seealso cref="Commands.AddHttpEndpoint"/>
    /// <seealso cref="Commands.RemoveHttpEndpoint"/>
    public partial class HttpEndpoint : ModelObject, IStaticModelObject
    {
        /// <summary>
        /// Namespace prefix used for RepRapFirmware HTTP requests
        /// </summary>
        public const string RepRapFirmwareNamespace = "rr_";

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
        /// <remarks>
        /// May be <see cref="RepRapFirmwareNamespace"/> to register root-level rr_ requests (to emulate RRF poll requests)
        /// </remarks>
        public string Namespace
        {
            get => _namespace;
			set => SetPropertyValue(ref _namespace, value);
        }
        private string _namespace = string.Empty;

        /// <summary>
        /// Path to the endpoint
        /// </summary>
        public string Path
        {
            get => _path;
			set => SetPropertyValue(ref _path, value);
        }
        private string _path = string.Empty;

        /// <summary>
        /// Whether this is a upload request
        /// </summary>
        /// <remarks>
        /// If set to true, the whole body payload is written to a temporary file and the file path is passed via the <see cref="Commands.ReceivedHttpRequest.Body"/> property
        /// </remarks>
        public bool IsUploadRequest
        {
            get => _isUploadRequest;
            set => SetPropertyValue(ref _isUploadRequest, value);
        }
        private bool _isUploadRequest;

        /// <summary>
        /// Path to the UNIX socket
        /// </summary>
        public string UnixSocket
        {
            get => _unixSocket;
			set => SetPropertyValue(ref _unixSocket, value);
        }
        private string _unixSocket = string.Empty;
    }
}
