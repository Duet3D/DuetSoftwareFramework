using DuetAPI.ObjectModel;
using DuetHttpClient.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DuetHttpClient.Connector
{
    /// <summary>
    /// Internal class to wrap HTTP requests
    /// </summary>
    internal abstract class BaseConnector : IAsyncDisposable
    {
        /// <summary>
        /// Protected constructor of this class
        /// </summary>
        /// <param name="baseUri">Base URI of the remote board</param>
        /// <param name="options">Connection options or null</param>
        protected BaseConnector(Uri baseUri, DuetHttpOptions options)
        {
            HttpClient.BaseAddress = baseUri;
            HttpClient.Timeout = Timeout.InfiniteTimeSpan;  // this isn't set to options.Timeout because HttpRequestMessage provides no way to override it
            Options = options;
        }

        /// <summary>
        /// HTTP cient of this connector
        /// </summary>
        public HttpClient HttpClient { get; } = new();

        /// <summary>
        /// Reconnect when the connection has been reset
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Asynchronous task</returns>
        protected abstract Task Reconnect(CancellationToken cancellationToken);

        /// <summary>
        /// Send a generic a HTTP request
        /// </summary>
        /// <param name="request">HTTP request to send</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>HTTP response</returns>
        protected virtual async ValueTask<HttpResponseMessage> SendRequest(HttpRequestMessage request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _terminateSession.Token);
            if (timeout != Timeout.InfiniteTimeSpan)
            {
                cts.CancelAfter(timeout);
            }

            HttpResponseMessage response = await HttpClient.SendAsync(request, cts.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                // Session is no longer valid, attempt to connect again
                await Reconnect(cancellationToken);
            }
            return response;
        }

        /// <summary>
        /// Cancellation token to terminate the session on demand
        /// </summary>
        protected readonly CancellationTokenSource _terminateSession = new();

        /// <summary>
        /// TCS to complete when the session task has been termianted
        /// </summary>
        protected readonly TaskCompletionSource _sessionTaskTerminated = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Object model of the remote board
        /// </summary>
        public ObjectModel Model { get; } = new();

        /// <summary>
        /// Options used for communication
        /// </summary>
        public DuetHttpOptions Options { get; }

        /// <summary>
        /// Send a G/M/T-code and return the G-code reply
        /// </summary>
        /// <param name="code">Code to send</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Code reply</returns>
        public abstract Task<string> SendCode(string code, CancellationToken cancellationToken = default);

        /// <summary>
        /// Upload arbitrary content to a file
        /// </summary>
        /// <param name="filename">Target filename</param>
        /// <param name="content">File content</param>
        /// <param name="lastModified">Last modified datetime. Ignored in SBC mode</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public abstract Task Upload(string filename, Stream content, DateTime? lastModified = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Delete a file or directory
        /// </summary>
        /// <param name="filename">Target filename</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public abstract Task Delete(string filename, CancellationToken cancellationToken = default);

        /// <summary>
        /// Move a file or directory
        /// </summary>
        /// <param name="from">Source file</param>
        /// <param name="to">Destination file</param>
        /// <param name="force">Overwrite file if it already exists</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public abstract Task Move(string from, string to, bool force = false, CancellationToken cancellationToken = default);

        /// <summary>
        /// Make a new directory
        /// </summary>
        /// <param name="directory">Target directory</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public abstract Task MakeDirectory(string directory, CancellationToken cancellationToken = default);

        /// <summary>
        /// Download a file
        /// </summary>
        /// <param name="filename">Name of the file to download</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Download response</returns>
        public abstract Task<HttpResponseMessage> Download(string filename, CancellationToken cancellationToken = default);

        /// <summary>
        /// Enumerate all files and directories in the given directory
        /// </summary>
        /// <param name="directory">Directory to query</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>List of all files and directories</returns>
        public abstract Task<IList<FileListItem>> GetFileList(string directory, CancellationToken cancellationToken = default);

        /// <summary>
        /// Get G-code file info
        /// </summary>
        /// <param name="filename">File to query</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>G-code file info</returns>
        public abstract Task<ParsedFileInfo> GetFileInfo(string filename, CancellationToken cancellationToken = default);

        // ** Plugin and system package calls are not supported (yet) **

        /// <summary>
        /// Abstract declaration of the DisposeAsync method
        /// </summary>
        public abstract ValueTask DisposeAsync();
    }
}
