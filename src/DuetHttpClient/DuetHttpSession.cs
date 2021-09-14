using DuetAPI.ObjectModel;
using DuetHttpClient.Connector;
using DuetHttpClient.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DuetHttpClient
{
    /// <summary>
    /// Class to maintain remote sessions with Duet controllers
    /// </summary>
    public sealed class DuetHttpSession : IAsyncDisposable
    {
        /// <summary>
        /// Connect to a remote Duet controller and create a new session
        /// </summary>
        /// <param name="baseUri">Base URI to the remote board</param>
        /// <param name="options">Connection options or null</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Duet session</returns>
        /// <exception cref="HttpRequestException">Board did not return a valid HTTP code</exception>
        /// <exception cref="InvalidPasswordException">Invalid password specified</exception>
        /// <exception cref="NoFreeSessionException">No free session available</exception>
        /// <exception cref="InvalidVersionException">Unsupported DSF version</exception>
        public static async Task<DuetHttpSession> ConnectAsync(Uri baseUri, DuetHttpOptions options = null, CancellationToken cancellationToken = default)
        {
            if (options == null)
            {
                // Use default settings if none are passed
                options = new();
            }

            try
            {
                PollConnector pollConnector = await PollConnector.ConnectAsync(baseUri, options, cancellationToken);
                return new DuetHttpSession(pollConnector);
            }
            catch (HttpRequestException)
            {
                // ignored
            }

            RestConnector restConnector = await RestConnector.ConnectAsync(baseUri, options, cancellationToken);
            return new DuetHttpSession(restConnector);
        }

        /// <summary>
        /// Connector providing HTTP functionality 
        /// </summary>
        private readonly BaseConnector _connector;

        /// <summary>
        /// Constructor of a new Duet session
        /// </summary>
        /// <param name="hostname"></param>
        /// <param name="port"></param>
        private DuetHttpSession(BaseConnector connector)
        {
            _connector = connector;
        }

        /// <summary>
        /// HTTP port of this machine
        /// </summary>
        public DuetHttpOptions Options { get => _connector.Options; }

        /// <summary>
        /// Object model of the remote machine
        /// </summary>
        /// <remarks>
        /// This is only kept up-to-date if <see cref="DuetHttpOptions.ObserveMessages"/> or <see cref="DuetHttpOptions.ObserveObjectModel"/> is set
        /// </remarks>
        public ObjectModel Model { get => _connector.Model; }

        /// <summary>
        /// Dispose this instance and the corresponding session
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public ValueTask DisposeAsync() => _connector.DisposeAsync();

        /// <summary>
        /// Send a G/M/T-code and return the G-code reply
        /// </summary>
        /// <param name="code">Code to send</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Code reply</returns>
        public Task<string> SendCode(string code, CancellationToken cancellationToken = default)
        {
            return _connector.SendCode(code, cancellationToken);
        }

        /// <summary>
        /// Upload arbitrary content to a file
        /// </summary>
        /// <param name="filename">Target filename</param>
        /// <param name="content">File content</param>
        /// <param name="lastModified">Last modified datetime. Ignored in SBC mode</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public Task Upload(string filename, Stream content, DateTime? lastModified = null, CancellationToken cancellationToken = default)
        {
            return _connector.Upload(filename, content, lastModified, cancellationToken);
        }

        /// <summary>
        /// Delete a file or directory
        /// </summary>
        /// <param name="filename">Target filename</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public Task Delete(string filename, CancellationToken cancellationToken = default)
        {
            return _connector.Delete(filename, cancellationToken);
        }

        /// <summary>
        /// Move a file or directory
        /// </summary>
        /// <param name="from">Source file</param>
        /// <param name="to">Destination file</param>
        /// <param name="force">Overwrite file if it already exists</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public Task Move(string from, string to, bool force = false, CancellationToken cancellationToken = default)
        {
            return _connector.Move(from, to, force, cancellationToken);
        }

        /// <summary>
        /// Make a new directory
        /// </summary>
        /// <param name="directory">Target directory</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public Task MakeDirectory(string directory, CancellationToken cancellationToken = default)
        {
            return _connector.MakeDirectory(directory, cancellationToken);
        }

        /// <summary>
        /// Download a file
        /// </summary>
        /// <param name="filename">Name of the file to download</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Download response</returns>
        public Task<HttpResponseMessage> Download(string filename, CancellationToken cancellationToken = default)
        {
            return _connector.Download(filename, cancellationToken);
        }

        /// <summary>
        /// Enumerate all files and directories in the given directory
        /// </summary>
        /// <param name="directory">Directory to query</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>List of all files and directories</returns>
        public Task<IList<FileListItem>> GetFileList(string directory, CancellationToken cancellationToken = default)
        {
            return _connector.GetFileList(directory, cancellationToken);
        }

        /// <summary>
        /// Get G-code file info
        /// </summary>
        /// <param name="filename">File to query</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>G-code file info</returns>
        public Task<ParsedFileInfo> GetFileInfo(string filename, CancellationToken cancellationToken = default)
        {
            return _connector.GetFileInfo(filename, cancellationToken);
        }

        // ** Plugin and system package calls are not supported (yet) **
    }
}
