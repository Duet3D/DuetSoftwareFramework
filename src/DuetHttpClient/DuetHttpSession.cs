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
        public static async Task<DuetHttpSession> ConnectAsync(Uri baseUri, DuetHttpOptions? options = null, CancellationToken cancellationToken = default)
        {
            // Use default settings if none are passed
            options ??= new DuetHttpOptions();

            try
            {
                PollConnector pollConnector = await PollConnector.ConnectAsync(baseUri, options, cancellationToken).ConfigureAwait(false);
                return new DuetHttpSession(pollConnector);
            }
            catch (HttpRequestException)
            {
                // ignored
            }

            RestConnector restConnector = await RestConnector.ConnectAsync(baseUri, options, cancellationToken).ConfigureAwait(false);
            return new DuetHttpSession(restConnector);
        }

        /// <summary>
        /// Connector providing HTTP functionality 
        /// </summary>
        private readonly BaseConnector _connector;

        /// <summary>
        /// Constructor of a new Duet session
        /// </summary>
        /// <param name="connector">Connector to use</param>
        private DuetHttpSession(BaseConnector connector) => _connector = connector;

        /// <summary>
        /// HTTP port of this machine
        /// </summary>
        public DuetHttpOptions Options => _connector.Options;

        /// <summary>
        /// Object model of the remote machine
        /// </summary>
        /// <remarks>
        /// This is only kept up-to-date if <see cref="DuetHttpOptions.ObserveMessages"/> or <see cref="DuetHttpOptions.ObserveObjectModel"/> is set
        /// </remarks>
        public ObjectModel Model => _connector.Model;

        /// <summary>
        /// Dispose this instance and the corresponding session
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public ValueTask DisposeAsync() => _connector.DisposeAsync();

        /// <summary>
        /// Wait for the object model to be up-to-date
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public Task WaitForModelUpdateAsync(CancellationToken cancellationToken = default)
        {
            return _connector.WaitForModelUpdateAsync(cancellationToken);
        }

        /// <summary>
        /// Send a G/M/T-code and return the G-code reply
        /// </summary>
        /// <param name="code">Code to send</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Code reply</returns>
        public Task<string> SendCodeAsync(string code, CancellationToken cancellationToken = default)
        {
            return _connector.SendCodeAsync(code, cancellationToken);
        }

        /// <summary>
        /// Upload arbitrary content to a file
        /// </summary>
        /// <param name="filename">Target filename</param>
        /// <param name="content">File content</param>
        /// <param name="lastModified">Last modified datetime. Ignored in SBC mode</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public Task UploadAsync(string filename, Stream content, DateTime? lastModified = null, CancellationToken cancellationToken = default)
        {
            return _connector.UploadAsync(filename, content, lastModified, cancellationToken);
        }

        /// <summary>
        /// Delete a file or directory
        /// </summary>
        /// <param name="filename">Target filename</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public Task DeleteAsync(string filename, CancellationToken cancellationToken = default)
        {
            return _connector.DeleteAsync(filename, cancellationToken);
        }

        /// <summary>
        /// Move a file or directory
        /// </summary>
        /// <param name="from">Source file</param>
        /// <param name="to">Destination file</param>
        /// <param name="force">Overwrite file if it already exists</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public Task MoveAsync(string from, string to, bool force = false, CancellationToken cancellationToken = default)
        {
            return _connector.MoveAsync(from, to, force, cancellationToken);
        }

        /// <summary>
        /// Make a new directory
        /// </summary>
        /// <param name="directory">Target directory</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public Task MakeDirectory(string directory, CancellationToken cancellationToken = default)
        {
            return _connector.MakeDirectoryAsync(directory, cancellationToken);
        }

        /// <summary>
        /// Download a file
        /// </summary>
        /// <param name="filename">Name of the file to download</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Download response</returns>
        public Task<HttpResponseMessage> DownloadAsync(string filename, CancellationToken cancellationToken = default)
        {
            return _connector.DownloadAsync(filename, cancellationToken);
        }

        /// <summary>
        /// Enumerate all files and directories in the given directory
        /// </summary>
        /// <param name="directory">Directory to query</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>List of all files and directories</returns>
        public Task<IList<FileListItem>> GetFileListAsync(string directory, CancellationToken cancellationToken = default)
        {
            return _connector.GetFileListAsync(directory, cancellationToken);
        }

        /// <summary>
        /// Get G-code file info
        /// </summary>
        /// <param name="filename">File to query</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>G-code file info</returns>
        public Task<GCodeFileInfo> GetFileInfoAsync(string filename, CancellationToken cancellationToken = default)
        {
            return _connector.GetFileInfoAsync(filename, false, cancellationToken);
        }

        /// <summary>
        /// Get G-code file info
        /// </summary>
        /// <param name="filename">File to query</param>
        /// <param name="readThumbnailContent">Whether thumbnail contents shall be parsed</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>G-code file info</returns>
        public Task<GCodeFileInfo> GetFileInfoAsync(string filename, bool readThumbnailContent, CancellationToken cancellationToken = default)
        {
            return _connector.GetFileInfoAsync(filename, readThumbnailContent, cancellationToken);
        }

        // ** Plugin and system package calls are not supported (yet) **
    }
}
