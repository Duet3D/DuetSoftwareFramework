using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetAPIClient;
using DuetWebServer.Singletons;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DuetWebServer.Controllers
{
    /// <summary>
    /// MVC Controller for /machine requests
    /// </summary>
    /// <remarks>
    /// Create a new controller instance
    /// </remarks>
    /// <param name="configuration">Launch configuration</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="applicationLifetime">Application lifecycle instance</param>
    [ApiController]
    [Authorize(Policy = Authorization.Policies.ReadOnly)]
    [Route("[controller]")]
    public class MachineController(IConfiguration configuration, ILogger<MachineController> logger, IHostApplicationLifetime applicationLifetime) : ControllerBase
    {
        /// <summary>
        /// App settings
        /// </summary>
        private readonly Settings _settings = configuration.Get<Settings>() ?? new();

        #region Logging
        /// <summary>
        /// Log an information
        /// </summary>
        /// <param name="message">Message</param>
        /// <param name="memberName">Method calling this method</param>
        private void LogInformation(string message, [CallerMemberName] string memberName = "")
        {
            logger.LogInformation("[{method}] {message}", memberName, message);
        }

        /// <summary>
        /// Log a warning
        /// </summary>
        /// <param name="message">Message</param>
        /// <param name="memberName">Method calling this method</param>
        private void LogWarning(string message, [CallerMemberName] string memberName = "")
        {
            logger.LogWarning("[{method}] {message}", memberName, message);
        }

        /// <summary>
        /// Log a warning
        /// </summary>
        /// <param name="exception">Exception</param>
        /// <param name="message">Message</param>
        /// <param name="memberName">Method calling this method</param>
        private void LogWarning(Exception? exception, string message, [CallerMemberName] string memberName = "")
        {
            logger.LogWarning(exception, "[{method}] {message}", memberName, message);
        }

        /// <summary>
        /// Log an error
        /// </summary>
        /// <param name="message">Message</param>
        /// <param name="memberName">Method calling this method</param>
        private void LogError(string message, [CallerMemberName] string memberName = "")
        {
            logger.LogError("[{method}] {message}", memberName, message);
        }
        #endregion

        #region Authorization
        /// <summary>
        /// GET /machine/connect
        /// Check the password and register a new session on success
        /// </summary>
        /// <param name="password">Password to check</param>
        /// <param name="sessionStorage">Session storage singleton</param>
        /// <returns>
        /// HTTP status code:
        /// (200) Session key
        /// (403) Forbidden
        /// (500) Generic error
        /// (502) Incompatible DCS version
        /// (503) DCS is unavailable
        /// </returns>
        [AllowAnonymous]
        [HttpGet("connect")]
        public async Task<IActionResult> Connect(string? password, [FromServices] ISessionStorage sessionStorage)
        {
            try
            {
                using CommandConnection connection = await BuildConnection();
                if ((_settings.OverrideWebPassword == null && await connection.CheckPassword(password ?? string.Empty)) ||
                    (_settings.OverrideWebPassword != null && _settings.OverrideWebPassword == (password ?? string.Empty)))
                {
                    int sessionId = await connection.AddUserSession(AccessLevel.ReadWrite, SessionType.HTTP, HttpContext.Connection.RemoteIpAddress!.ToString());
                    string sessionKey = sessionStorage.MakeSessionKey(sessionId, string.Empty, true);

                    string jsonResponse = JsonSerializer.Serialize(new
                    {
                        SessionKey = sessionKey
                    }, JsonHelper.DefaultJsonOptions);
                    return Content(jsonResponse, "application/json");
                }
                return Forbid();
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException!;
                }
                if (e is IncompatibleVersionException)
                {
                    LogError("Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    if (System.IO.File.Exists(_settings.StartErrorFile))
                    {
                        string startError = await System.IO.File.ReadAllTextAsync(_settings.StartErrorFile);
                        LogError(startError);
                        return StatusCode(503, startError);
                    }

                    LogError("DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                LogWarning(e, "Failed to handle connect request");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// GET /machine/noop
        /// Do nothing. May be used to ping the machine or to keep the HTTP session alive
        /// </summary>
        /// <returns>
        /// HTTP status code:
        /// (204) No Content
        /// </returns>
        [HttpGet("noop")]
        public IActionResult Noop() => NoContent();

        /// <summary>
        /// GET /machine/disconnect
        /// Remove the current HTTP session again
        /// </summary>
        /// <param name="sessionStorage">Session storage singleton</param>
        /// <returns>
        /// HTTP status code:
        /// (204) No Content
        /// (500) Generic error
        /// (502) Incompatible DCS version
        /// (503) DCS is unavailable
        /// </returns>
        [AllowAnonymous]
        [HttpGet("disconnect")]
        public async Task<IActionResult> Disconnect([FromServices] ISessionStorage sessionStorage)
        {
            try
            {
                if (HttpContext.User is not null)
                {
                    // Remove the internal session
                    int sessionId = sessionStorage.RemoveTicket(HttpContext.User);

                    // Remove the DSF user session again
                    if (sessionId > 0)
                    {
                        using CommandConnection connection = await BuildConnection();
                        await connection.RemoveUserSession(sessionId);
                    }
                }
                return NoContent();
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException!;
                }
                if (e is IncompatibleVersionException)
                {
                    LogError("Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    if (System.IO.File.Exists(_settings.StartErrorFile))
                    {
                        string startError = await System.IO.File.ReadAllTextAsync(_settings.StartErrorFile);
                        LogError(startError);
                        return StatusCode(503, startError);
                    }

                    LogError("DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                LogWarning(e, "Failed to handle disconnect request");
                return StatusCode(500, e.Message);
            }
        }
        #endregion

        #region General requests
        /// <summary>
        /// GET /machine/model
        /// - and -
        /// GET /machine/status
        /// Retrieve the full object model as JSON.
        /// </summary>
        /// <returns>
        /// HTTP status code:
        /// (200) Object model as application/json
        /// (500) Generic error
        /// (502) Incompatible DCS version
        /// (503) DCS is unavailable
        /// </returns>
        [HttpGet("model")]
        [HttpGet("status")]
        public async Task<IActionResult> Model()
        {
            try
            {
                using CommandConnection connection = await BuildConnection();
                string machineModel = await connection.GetSerializedObjectModel();
                return Content(machineModel, "application/json");
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException!;
                }
                if (e is IncompatibleVersionException)
                {
                    LogError("Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    if (System.IO.File.Exists(_settings.StartErrorFile))
                    {
                        string startError = await System.IO.File.ReadAllTextAsync(_settings.StartErrorFile);
                        LogError(startError);
                        return StatusCode(503, startError);
                    }

                    LogError("DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                LogWarning(e, "Failed to retrieve object model");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// POST /machine/code
        /// Execute plain G/M/T-code(s) from the request body and return the G-code response when done.
        /// </summary>
        /// <param name="sessionStorage">Session storage singleton</param>
        /// <param name="async">Execute code asynchronously (don't wait for a code result)</param>
        /// <returns>
        /// HTTP status code:
        /// (200) G-Code response as text/plain
        /// (500) Generic error
        /// (502) Incompatible DCS version
        /// (503) DCS is unavailable
        /// </returns>
        [HttpPost("code")]
        [Authorize(Policy = Authorization.Policies.ReadWrite)]
        public async Task<IActionResult> DoCode([FromServices] ISessionStorage sessionStorage, bool async = false)
        {
            string code;
            {
                using StreamReader reader = new(Request.Body, Encoding.UTF8);
                code = await reader.ReadToEndAsync();
            }

            try
            {
                if (!async)
                {
                    sessionStorage.SetLongRunningHttpRequest(HttpContext.User, true);
                }

                try
                {
                    using CommandConnection connection = await BuildConnection();
                    LogInformation($"Executing code '{code}'");
                    return Content(await connection.PerformSimpleCode(code, CodeChannel.HTTP, async));
                }
                finally
                {
                    if (!async)
                    {
                        sessionStorage.SetLongRunningHttpRequest(HttpContext.User, false);
                    }
                }
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException!;
                }
                if (e is IncompatibleVersionException)
                {
                    LogError("Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    if (System.IO.File.Exists(_settings.StartErrorFile))
                    {
                        string startError = await System.IO.File.ReadAllTextAsync(_settings.StartErrorFile);
                        LogError(startError);
                        return StatusCode(503, startError);
                    }

                    LogError("DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                LogWarning(e, "Failed to perform code");
                return StatusCode(500, e.Message);
            }
        }
        #endregion

        #region File requests
        /// <summary>
        /// GET /machine/file/{filename}
        /// Download the specified file.
        /// </summary>
        /// <param name="filename">File to download</param>
        /// <returns>
        /// HTTP status code:
        /// (200) File content
        /// (404) File not found
        /// (500) Generic error
        /// (502) Incompatible DCS version
        /// (503) DCS is unavailable
        /// </returns>
        [HttpGet("file/{*filename}")]
        public async Task<IActionResult> DownloadFile(string filename)
        {
            filename = HttpUtility.UrlDecode(filename);

            string resolvedPath = "n/a";
            try
            {
                resolvedPath = await ResolvePath(filename);
                if (!System.IO.File.Exists(resolvedPath))
                {
                    LogWarning($"Could not find file {filename} (resolved to {resolvedPath})");
                    return NotFound(HttpUtility.UrlPathEncode(filename));
                }

                FileStream stream = new(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return File(stream, "application/octet-stream");
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException!;
                }
                if (e is IncompatibleVersionException)
                {
                    LogError("Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    if (System.IO.File.Exists(_settings.StartErrorFile))
                    {
                        string startError = await System.IO.File.ReadAllTextAsync(_settings.StartErrorFile);
                        LogError(startError);
                        return StatusCode(503, startError);
                    }

                    LogError("DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                LogWarning(e, $"Failed download file {filename} (resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// PUT /machine/file/{filename}?timeModified={timeModified}
        /// Upload a file from the HTTP body and create the subdirectories if necessary
        /// </summary>
        /// <param name="filename">Destination of the file to upload</param>
        /// <param name="timeModified">Optional time indicating when the file was last modified</param>
        /// <param name="sessionStorage">Session storage singleton</param>
        /// <returns>
        /// HTTP status code:
        /// (201) File created
        /// (500) Generic error occurred
        /// (502) Incompatible DCS version
        /// (503) DCS is unavailable
        /// </returns>
        [Authorize(Policy = Authorization.Policies.ReadWrite)]
        [DisableRequestSizeLimit]
        [HttpPut("file/{*filename}")]
        public async Task<IActionResult> UploadFile(string filename, DateTime? timeModified, [FromServices] ISessionStorage sessionStorage)
        {
            filename = HttpUtility.UrlDecode(filename);

            string resolvedPath = "n/a";
            try
            {
                sessionStorage.SetLongRunningHttpRequest(HttpContext.User, true);
                try
                {
                    resolvedPath = await ResolvePath(filename);

                    // Create directory if necessary
                    string directory = Path.GetDirectoryName(resolvedPath)!;
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    string partFile = resolvedPath + ".part";
                    try
                    {
                        // Write .part file
                        await using (FileStream stream = new(partFile, FileMode.Create, FileAccess.Write))
                        {
                            await Request.Body.CopyToAsync(stream);
                        }

                        // Move it into place
                        System.IO.File.Move(partFile, resolvedPath, true);

                        // Change the datetime of the file if possible
                        if (timeModified is not null)
                        {
                            System.IO.File.SetLastWriteTime(resolvedPath, timeModified.Value);
                        }
                    }
                    catch
                    {
                        // Delete the file on error
                        System.IO.File.Delete(partFile);
                        throw;
                    }

                    return Created(HttpUtility.UrlPathEncode(filename), null);
                }
                finally
                {
                    sessionStorage.SetLongRunningHttpRequest(HttpContext.User, false);
                }
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException!;
                }
                if (e is IncompatibleVersionException)
                {
                    LogError("Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    if (System.IO.File.Exists(_settings.StartErrorFile))
                    {
                        string startError = await System.IO.File.ReadAllTextAsync(_settings.StartErrorFile);
                        LogError(startError);
                        return StatusCode(503, startError);
                    }

                    LogError("DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                LogWarning(e, $"Failed upload file {filename} (resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// GET /machine/fileinfo/{filename}?readThumbnailContent=true/false
        /// Parse a given G-code file and return information about this job file as a JSON object.
        /// </summary>
        /// <param name="filename">G-code file to analyze</param>
        /// <param name="readThumbnailContent">Whether thumbnail content may be read</param>
        /// <returns>
        /// HTTP status code:
        /// (200) File info as application/json
        /// (404) File not found
        /// (500) Generic error
        /// (502) Incompatible DCS
        /// (503) DCS is unavailable
        /// </returns>
        [HttpGet("fileinfo/{*filename}")]
        public async Task<IActionResult> GetFileInfo(string filename, bool readThumbnailContent = false)
        {
            filename = HttpUtility.UrlDecode(filename);

            string resolvedPath = "n/a";
            try
            {
                resolvedPath = await ResolvePath(filename);
                if (!System.IO.File.Exists(resolvedPath))
                {
                    LogWarning($"Could not find file {filename} (resolved to {resolvedPath})");
                    return NotFound(HttpUtility.UrlPathEncode(filename));
                }

                using CommandConnection connection = await BuildConnection();
                var info = await connection.GetFileInfo(resolvedPath, readThumbnailContent);

                string json = JsonSerializer.Serialize(info, JsonHelper.DefaultJsonOptions);
                return Content(json, "application/json");
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException!;
                }
                if (e is IncompatibleVersionException)
                {
                    LogError("Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    if (System.IO.File.Exists(_settings.StartErrorFile))
                    {
                        string startError = await System.IO.File.ReadAllTextAsync(_settings.StartErrorFile);
                        LogError(startError);
                        return StatusCode(503, startError);
                    }

                    LogError("DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                LogWarning(e, $"Failed to retrieve file info for {filename} (resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            }
        }
        #endregion
        
        #region Shared File and Directory requests
        /// <summary>
        /// DELETE /machine/file/{filename}
        /// Delete the given file or directory.
        /// </summary>
        /// <param name="filename">File or directory to delete</param>
        /// <param name="recursive">Whether the directory shall be deleted recursively</param>
        /// <returns>
        /// HTTP status code:
        /// (204) File or directory deleted
        /// (404) File not found
        /// (500) Generic error
        /// (502) Incompatible DCS version
        /// (503) DCS is unavailable
        /// </returns>
        [Authorize(Policy = Authorization.Policies.ReadWrite)]
        [HttpDelete("file/{*filename}")]
        public async Task<IActionResult> DeleteFileOrDirectory(string filename, bool recursive = false)
        {
            filename = HttpUtility.UrlDecode(filename);

            string resolvedPath = "n/a";
            try
            {
                resolvedPath = await ResolvePath(filename);

                if (Directory.Exists(resolvedPath))
                {
                    Directory.Delete(resolvedPath, recursive);
                    return NoContent();
                }

                if (System.IO.File.Exists(resolvedPath))
                {
                    System.IO.File.Delete(resolvedPath);
                    return NoContent();
                }

                LogWarning($"Could not find file {filename} (resolved to {resolvedPath})");
                return NotFound(HttpUtility.UrlPathEncode(filename));
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException!;
                }
                if (e is IncompatibleVersionException)
                {
                    LogError("Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    if (System.IO.File.Exists(_settings.StartErrorFile))
                    {
                        string startError = await System.IO.File.ReadAllTextAsync(_settings.StartErrorFile);
                        LogError(startError);
                        return StatusCode(503, startError);
                    }

                    LogError("DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                LogWarning(e, $"Failed to delete file {filename} (resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// Move a file or directory from a to b
        /// </summary>
        /// <param name="from">Source path</param>
        /// <param name="to">Destination path</param>
        /// <param name="force">Delete existing file (optional, default false)</param>
        /// <returns>
        /// HTTP status code:
        /// (204) File or directory moved
        /// (404) File or directory not found
        /// (500) Generic error
        /// (502) Incompatible DCS version
        /// (503) DCS is unavailable
        /// </returns>
        [Authorize(Policy = Authorization.Policies.ReadWrite)]
        [HttpPost("file/move")]
        public async Task<IActionResult> MoveFileOrDirectory([FromForm] string from, [FromForm] string to, [FromForm] bool force = false)
        {
            string source = "n/a", destination = "n/a";
            try
            {
                source = await ResolvePath(from);
                destination = await ResolvePath(to);

                // Deal with directories
                if (Directory.Exists(source))
                {
                    if (Directory.Exists(destination))
                    {
                        if (force)
                        {
                            Directory.Delete(destination);
                        }
                        else
                        {
                            return Conflict();
                        }
                    }
                    
                    Directory.Move(source, destination);
                    return NoContent();
                }
                
                // Deal with files
                if (System.IO.File.Exists(source))
                {
                    if (System.IO.File.Exists(destination))
                    {
                        if (force)
                        {
                            System.IO.File.Delete(destination);
                        }
                        else
                        {
                            return Conflict();
                        }
                    }
                    
                    System.IO.File.Move(source, destination);
                    return NoContent();
                }

                return force ? NoContent() : NotFound(HttpUtility.UrlPathEncode(from));
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException!;
                }
                if (e is IncompatibleVersionException)
                {
                    LogError("Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    if (System.IO.File.Exists(_settings.StartErrorFile))
                    {
                        string startError = await System.IO.File.ReadAllTextAsync(_settings.StartErrorFile);
                        LogError(startError);
                        return StatusCode(503, startError);
                    }

                    LogError("DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                LogWarning(e, $"Failed to move file {from} to {to} (resolved to {source} and {destination})");
                return StatusCode(500, e.Message);
            }
        }
        #endregion
        
        #region Directory requests
        /// <summary>
        /// GET /machine/directory/{directory}
        /// Get a file list of the specified directory
        /// </summary>
        /// <param name="directory">Directory to query</param>
        /// <returns>
        /// HTTP status code:
        /// (200) File list as application/json
        /// (404) Directory not found
        /// (500) Generic error
        /// (502) Incompatible DCS version
        /// (503) DCS is unavailable
        /// </returns>
        [HttpGet("directory/{*directory}")]
        public async Task<IActionResult> GetFileList(string? directory)
        {
            directory = HttpUtility.UrlDecode(directory);

            string resolvedPath = "n/a";
            try
            {
                resolvedPath = await ResolvePath(directory ?? string.Empty);
                if (!Directory.Exists(resolvedPath))
                {
                    LogWarning($"Could not find directory {directory} (resolved to {resolvedPath})");
                    return NotFound(HttpUtility.UrlPathEncode(directory));
                }
                return File(FileLists.GetFileListUtf8(directory ?? string.Empty, resolvedPath), "application/json");
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException!;
                }
                if (e is IncompatibleVersionException)
                {
                    LogError("Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    if (System.IO.File.Exists(_settings.StartErrorFile))
                    {
                        string startError = await System.IO.File.ReadAllTextAsync(_settings.StartErrorFile);
                        LogError(startError);
                        return StatusCode(503, startError);
                    }

                    LogError("DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                LogWarning(e, $"Failed to retrieve file list for {directory} (resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// PUT /machine/directory/{directory}
        /// Create the given directory.
        /// </summary>
        /// <param name="directory">Directory to create</param>
        /// <returns>
        /// HTTP status code:
        /// (204) Directory created
        /// (500) Generic error
        /// (502) Incompatible DCS version
        /// (503) DCS is unavailable
        /// </returns>
        [Authorize(Policy = Authorization.Policies.ReadWrite)]
        [HttpPut("directory/{*directory}")]
        public async Task<IActionResult> CreateDirectory(string directory)
        {
            directory = HttpUtility.UrlDecode(directory);

            string resolvedPath = "n/a";
            try
            {
                resolvedPath = await ResolvePath(directory);
                Directory.CreateDirectory(resolvedPath);
                return Created(HttpUtility.UrlPathEncode(directory), null);
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException!;
                }
                if (e is IncompatibleVersionException)
                {
                    LogError("Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    if (System.IO.File.Exists(_settings.StartErrorFile))
                    {
                        string startError = await System.IO.File.ReadAllTextAsync(_settings.StartErrorFile);
                        LogError(startError);
                        return StatusCode(503, startError);
                    }

                    LogError("DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                LogWarning(e, $"Failed to create directory {directory} (resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            }
        }
        #endregion

        #region Plugins
        /// <summary>
        /// PUT /machine/plugin
        /// Install or upgrade a plugin ZIP file
        /// </summary>
        /// <param name="sessionStorage">Session storage singleton</param>
        /// <returns>
        /// HTTP status code:
        /// (204) No content
        /// (500) Generic error occurred
        /// (502) Incompatible DCS version
        /// (503) DCS is unavailable
        /// </returns>
        [Authorize(Policy = Authorization.Policies.ReadWrite)]
        [DisableRequestSizeLimit]
        [HttpPut("plugin")]
        public async Task<IActionResult> InstallPlugin([FromServices] ISessionStorage sessionStorage)
        {
            string zipFile = Path.GetTempFileName();
            try
            {
                sessionStorage.SetLongRunningHttpRequest(HttpContext.User, true);
                try
                {
                    // Write ZIP file
                    await using (FileStream stream = new(zipFile, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await Request.Body.CopyToAsync(stream);
                    }

                    // Install it
                    using CommandConnection connection = await BuildConnection();
                    await connection.InstallPlugin(zipFile);

                    return NoContent();
                }
                catch (Exception e)
                {
                    if (e is AggregateException ae)
                    {
                        e = ae.InnerException!;
                    }
                    if (e is IncompatibleVersionException)
                    {
                        LogError("Incompatible DCS version");
                        return StatusCode(502, "Incompatible DCS version");
                    }
                    if (e is SocketException)
                    {
                        if (System.IO.File.Exists(_settings.StartErrorFile))
                        {
                            string startError = await System.IO.File.ReadAllTextAsync(_settings.StartErrorFile);
                            LogError(startError);
                            return StatusCode(503, startError);
                        }

                        LogError("DCS is not started");
                        return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                    }
                    LogWarning(e, $"Failed to upload ZIP file to {zipFile}");
                    return StatusCode(500, e.Message);
                }
            }
            finally
            {
                sessionStorage.SetLongRunningHttpRequest(HttpContext.User, false);
                System.IO.File.Delete(zipFile);
            }
        }

        /// <summary>
        /// DELETE /machine/plugin
        /// Uninstall a plugin
        /// </summary>
        /// <param name="sessionStorage">Session storage singleton</param>
        /// <returns>
        /// HTTP status code:
        /// (204) No content
        /// (500) Generic error occurred
        /// (502) Incompatible DCS version
        /// (503) DCS is unavailable
        /// </returns>
        [Authorize(Policy = Authorization.Policies.ReadWrite)]
        [HttpDelete("plugin")]
        public async Task<IActionResult> UninstallPlugin([FromServices] ISessionStorage sessionStorage)
        {
            try
            {
                sessionStorage.SetLongRunningHttpRequest(HttpContext.User, true);
                try
                {
                    // Get the plugin name
                    string pluginName;
                    using (StreamReader reader = new(HttpContext.Request.Body))
                    {
                        pluginName = await reader.ReadToEndAsync();
                    }

                    // Uninstall it
                    using CommandConnection connection = await BuildConnection();
                    await connection.UninstallPlugin(pluginName);

                    return NoContent();
                }
                finally
                {
                    sessionStorage.SetLongRunningHttpRequest(HttpContext.User, false);
                }
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException!;
                }
                if (e is IncompatibleVersionException)
                {
                    LogError("Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    if (System.IO.File.Exists(_settings.StartErrorFile))
                    {
                        string startError = await System.IO.File.ReadAllTextAsync(_settings.StartErrorFile);
                        LogError(startError);
                        return StatusCode(503, startError);
                    }

                    LogError("DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                LogWarning(e, "Failed to uninstall plugin");
                return StatusCode(500, e.Message);
            }
        }

#pragma warning disable IDE1006 // Naming Styles
        /// <summary>
        /// Private class for wrapping plugin data patch instructions
        /// </summary>
        private class PluginPatchInstruction
        {
            /// <summary>
            /// Plugin to change
            /// </summary>
            public string plugin { get; set; } = string.Empty;

            /// <summary>
            /// Key to change
            /// </summary>
            public string key { get; set; } = string.Empty;

            /// <summary>
            /// Target value
            /// </summary>
            public JsonElement value { get; set; }
        }
#pragma warning restore IDE1006 // Naming Styles

        /// <summary>
        /// PATCH /machine/plugin
        /// Set plugin data in the object model if there is no SBC executable.
        /// </summary>
        /// <returns>
        /// HTTP status code:
        /// (204) No content
        /// (500) Generic error occurred
        /// (502) Incompatible DCS version
        /// (503) DCS is unavailable
        /// </returns>
        [Authorize(Policy = Authorization.Policies.ReadWrite)]
        [DisableRequestSizeLimit]
        [HttpPatch("plugin")]
        public async Task<IActionResult> SetPluginData()
        {
            try
            {
                PluginPatchInstruction instruction = (await JsonSerializer.DeserializeAsync<PluginPatchInstruction>(HttpContext.Request.Body))!;

                using CommandConnection connection = await BuildConnection();
                ObjectModel model = await connection.GetObjectModel();
                if (model.Plugins.TryGetValue(instruction.plugin, out Plugin plugin))
                {
                    if (!string.IsNullOrEmpty(plugin.SbcExecutable))
                    {
                        LogWarning($"Tried to set plugin data for {plugin.Id} but it has an SBC executable set");
                        return Forbid();
                    }

                    await connection.SetPluginData(instruction.key, instruction.value, instruction.plugin);
                    return NoContent();
                }
                return NotFound();
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException!;
                }
                if (e is IncompatibleVersionException)
                {
                    LogError("Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    if (System.IO.File.Exists(_settings.StartErrorFile))
                    {
                        string startError = await System.IO.File.ReadAllTextAsync(_settings.StartErrorFile);
                        LogError(startError);
                        return StatusCode(503, startError);
                    }

                    LogError("DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                LogWarning(e, "Failed to set plugin data");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// POST /machine/startPlugin
        /// Start a plugin on the SBC
        /// </summary>
        /// <returns>
        /// HTTP status code:
        /// (204) No content
        /// (500) Generic error occurred
        /// (502) Incompatible DCS version
        /// (503) DCS is unavailable
        /// </returns>
        [Authorize(Policy = Authorization.Policies.ReadWrite)]
        [HttpPost("startPlugin")]
        public async Task<IActionResult> StartPlugin()
        {
            try
            {
                // Get the plugin name
                string pluginName;
                using (StreamReader reader = new(HttpContext.Request.Body))
                {
                    pluginName = await reader.ReadToEndAsync();
                }

                // Start it
                using CommandConnection connection = await BuildConnection();
                await connection.StartPlugin(pluginName);

                return NoContent();
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException!;
                }
                if (e is IncompatibleVersionException)
                {
                    LogError("Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    if (System.IO.File.Exists(_settings.StartErrorFile))
                    {
                        string startError = await System.IO.File.ReadAllTextAsync(_settings.StartErrorFile);
                        LogError(startError);
                        return StatusCode(503, startError);
                    }

                    LogError("DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                LogWarning(e, "Failed to start plugin");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// POST /machine/stopPlugin
        /// Stop a plugin on the SBC
        /// </summary>
        /// <returns>
        /// HTTP status code:
        /// (204) No content
        /// (500) Generic error occurred
        /// (502) Incompatible DCS version
        /// (503) DCS is unavailable
        /// </returns>
        [Authorize(Policy = Authorization.Policies.ReadWrite)]
        [HttpPost("stopPlugin")]
        public async Task<IActionResult> StopPlugin()
        {
            try
            {
                // Get the plugin name
                string pluginName;
                using (StreamReader reader = new(HttpContext.Request.Body))
                {
                    pluginName = await reader.ReadToEndAsync();
                }

                // Stop it
                using CommandConnection connection = await BuildConnection();
                await connection.StopPlugin(pluginName);

                return NoContent();
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException!;
                }
                if (e is IncompatibleVersionException)
                {
                    LogError("Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    if (System.IO.File.Exists(_settings.StartErrorFile))
                    {
                        string startError = await System.IO.File.ReadAllTextAsync(_settings.StartErrorFile);
                        LogError(startError);
                        return StatusCode(503, startError);
                    }

                    LogError("DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                LogWarning(e, "Failed to stop plugin");
                return StatusCode(500, e.Message);
            }
        }
        #endregion

        #region System packages
        /// <summary>
        /// PUT /machine/systemPackage
        /// Install or upgrade a system package
        /// </summary>
        /// <param name="sessionStorage">Session storage singleton</param>
        /// <returns>
        /// HTTP status code:
        /// (204) No content
        /// (500) Generic error occurred
        /// (502) Incompatible DCS version
        /// (503) DCS is unavailable
        /// </returns>
        [Authorize(Policy = Authorization.Policies.ReadWrite)]
        [DisableRequestSizeLimit]
        [HttpPut("systemPackage")]
        public async Task<IActionResult> InstallSystemPackage([FromServices] ISessionStorage sessionStorage)
        {
            string packageFile = Path.GetTempFileName();
            try
            {
                sessionStorage.SetLongRunningHttpRequest(HttpContext.User, true);
                try
                {
                    // Write package file
                    await using (FileStream stream = new(packageFile, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await Request.Body.CopyToAsync(stream);
                    }

                    // Install it
                    try
                    {
                        using CommandConnection connection = await BuildConnection();
                        await connection.InstallSystemPackage(packageFile, applicationLifetime.ApplicationStopping);
                    }
                    catch (OperationCanceledException)
                    {
                        LogWarning("Application is shutting down due to system package update");
                    }
                    return NoContent();
                }
                catch (Exception e)
                {
                    if (e is AggregateException ae)
                    {
                        e = ae.InnerException!;
                    }
                    if (e is IncompatibleVersionException)
                    {
                        LogError("Incompatible DCS version");
                        return StatusCode(502, "Incompatible DCS version");
                    }
                    if (e is SocketException)
                    {
                        if (System.IO.File.Exists(_settings.StartErrorFile))
                        {
                            string startError = await System.IO.File.ReadAllTextAsync(_settings.StartErrorFile);
                            LogError(startError);
                            return StatusCode(503, startError);
                        }

                        LogError("DCS is not started");
                        return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                    }
                    LogWarning(e, $"Failed to upload package file to {packageFile}");
                    return StatusCode(500, e.Message);
                }
            }
            finally
            {
                sessionStorage.SetLongRunningHttpRequest(HttpContext.User, false);
                System.IO.File.Delete(packageFile);
            }
        }

        /// <summary>
        /// DELETE /machine/systemPackage
        /// Uninstall a system package
        /// </summary>
        /// <param name="sessionStorage">Session storage singleton</param>
        /// <returns>
        /// HTTP status code:
        /// (204) No content
        /// (500) Generic error occurred
        /// (502) Incompatible DCS version
        /// (503) DCS is unavailable
        /// </returns>
        [Authorize(Policy = Authorization.Policies.ReadWrite)]
        [HttpDelete("systemPackage")]
        public async Task<IActionResult> UninstallSystemPackage([FromServices] ISessionStorage sessionStorage)
        {
            try
            {
                sessionStorage.SetLongRunningHttpRequest(HttpContext.User, true);
                try
                {
                    // Get the plugin name
                    string package;
                    using (StreamReader reader = new(HttpContext.Request.Body))
                    {
                        package = await reader.ReadToEndAsync();
                    }

                    // Uninstall it
                    using CommandConnection connection = await BuildConnection();
                    await connection.UninstallSystemPackage(package);

                    return NoContent();
                }
                finally
                {
                    sessionStorage.SetLongRunningHttpRequest(HttpContext.User, false);
                }
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException!;
                }
                if (e is IncompatibleVersionException)
                {
                    LogError("Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    if (System.IO.File.Exists(_settings.StartErrorFile))
                    {
                        string startError = await System.IO.File.ReadAllTextAsync(_settings.StartErrorFile);
                        LogError(startError);
                        return StatusCode(503, startError);
                    }

                    LogError("DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                LogWarning(e, "Failed to uninstall system package");
                return StatusCode(500, e.Message);
            }
        }
        #endregion

        private async Task<CommandConnection> BuildConnection()
        {
            CommandConnection connection = new();
            await connection.Connect(_settings.SocketPath);
            return connection;
        }

        private async Task<string> ResolvePath(string path)
        {
            using CommandConnection connection = await BuildConnection();
            return await connection.ResolvePath(path);
        }
    }
}
