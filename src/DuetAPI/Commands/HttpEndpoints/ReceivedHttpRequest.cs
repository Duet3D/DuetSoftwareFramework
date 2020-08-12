using System.Collections.Generic;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Notification sent by the webserver when a new HTTP request is received
    /// </summary>
    public sealed class ReceivedHttpRequest
    {
        /// <summary>
        /// Identifier of the corresponding user session. This is -1 if it is an anonymous request
        /// </summary>
        /// <seealso cref="ObjectModel.UserSession"/>
        public int SessionId { get; set; }

        /// <summary>
        /// List of HTTP query pairs
        /// </summary>
        public Dictionary<string, string> Queries { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// List of HTTP header pairs
        /// </summary>
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Type of the body content
        /// </summary>
        public string ContentType { get; set; }

        /// <summary>
        /// Body content as plain text
        /// </summary>
        public string Body { get; set; }
    }
}
