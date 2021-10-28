using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using DuetAPI;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetAPIClient;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DuetWebServer.Controllers
{
    /// <summary>
    /// MVC Controller for /machine requests
    /// </summary>
    [ApiController]
    [Authorize(Policy = Authorization.Policies.ReadOnly)]
    [Route("[controller]")]
    public class MachineController : ControllerBase
    {
        /// <summary>
        /// App configuration
        /// </summary>
        private readonly IConfiguration _configuration;

        /// <summary>
        /// Logger instance
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// Create a new controller instance
        /// </summary>
        /// <param name="configuration">Launch configuration</param>
        /// <param name="logger">Logger instance</param>
        public MachineController(IConfiguration configuration, ILogger<MachineController> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        #region Authorization
        // TODO Add challenge request here returning remote IP address and allow hashed password for Connect

        /// <summary>
        /// GET /machine/connect
        /// Check the password and register a new session on success
        /// </summary>
        /// <returns>HTTP status code: (200) Session key (403) Forbidden (500) Generic error (502) Incompatible DCS version (503) DCS is unavailable</returns>
        [AllowAnonymous]
        [HttpGet("connect")]
        public async Task<IActionResult> Connect(string password)
        {
            try
            {
                using CommandConnection connection = await BuildConnection();
                if (await connection.CheckPassword(password))
                {
                    int sessionId = await connection.AddUserSession(AccessLevel.ReadWrite, SessionType.HTTP, HttpContext.Connection.RemoteIpAddress.ToString());
                    string sessionKey = Authorization.Sessions.MakeSessionKey(sessionId, true);

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
                    e = ae.InnerException;
                }
                if (e is IncompatibleVersionException)
                {
                    _logger.LogError($"[{nameof(Connect)}] Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    _logger.LogError($"[{nameof(Connect)}] DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                _logger.LogWarning(e, $"[{nameof(Connect)}] Failed to retrieve status");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// GET /machine/noop
        /// Do nothing. May be used to ping the machine or to keep the HTTP session alive
        /// </summary>
        /// <returns>HTTP status code: (204) No Content</returns>
        [HttpGet("noop")]
        public IActionResult Noop() => NoContent();

        /// <summary>
        /// GET /machine/disconnect
        /// Remove the current HTTP session again
        /// </summary>
        /// <returns>HTTP status code: (204) No Content (500) Generic error (502) Incompatible DCS version (503) DCS is unavailable</returns>
        [AllowAnonymous]
        [HttpGet("disconnect")]
        public async Task<IActionResult> Disconnect()
        {
            try
            {
                if (HttpContext.User != null)
                {
                    // Remove the internal session
                    int sessionId = Authorization.Sessions.RemoveTicket(HttpContext.User);

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
                    e = ae.InnerException;
                }
                if (e is IncompatibleVersionException)
                {
                    _logger.LogError($"[{nameof(Disconnect)}] Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    _logger.LogError($"[{nameof(Disconnect)}] DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                _logger.LogWarning(e, $"[{nameof(Disconnect)}] Failed to retrieve status");
                return StatusCode(500, e.Message);
            }
        }
        #endregion

        #region General requests
        /// <summary>
        /// GET /machine/status
        /// Retrieve the full object model as JSON.
        /// </summary>
        /// <returns>HTTP status code: (200) Machine object model as application/json (500) Generic error (502) Incompatible DCS version (503) DCS is unavailable</returns>
        [HttpGet("status")]
        public async Task<IActionResult> Status()
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
                    e = ae.InnerException;
                }
                if (e is IncompatibleVersionException)
                {
                    _logger.LogError($"[{nameof(Status)}] Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    _logger.LogError($"[{nameof(Status)}] DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                _logger.LogWarning(e, $"[{nameof(Status)}] Failed to retrieve status");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// POST /machine/code
        /// Execute plain G/M/T-code(s) from the request body and return the G-code response when done.
        /// </summary>
        /// <returns>HTTP status code: (200) G-Code response as text/plain (500) Generic error (502) Incompatible DCS version (503) DCS is unavailable</returns>
        [HttpPost("code")]
        [Authorize(Policy = Authorization.Policies.ReadWrite)]
        public async Task<IActionResult> DoCode()
        {
            string code;
            using (StreamReader reader = new(Request.Body, Encoding.UTF8))
            {
                code = await reader.ReadToEndAsync();
            }

            try
            {
                Authorization.Sessions.SetLongRunningHttpRequest(HttpContext.User, true);
                try
                {
                    using CommandConnection connection = await BuildConnection();
                    _logger.LogInformation($"[{nameof(DoCode)}] Executing code '{code}'");
                    return Content(await connection.PerformSimpleCode(code, CodeChannel.HTTP));
                }
                finally
                {
                    Authorization.Sessions.SetLongRunningHttpRequest(HttpContext.User, false);
                }
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException;
                }
                if (e is IncompatibleVersionException)
                {
                    _logger.LogError($"[{nameof(DoCode)}] Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    _logger.LogError($"[{nameof(DoCode)}] DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                _logger.LogWarning(e, $"[{nameof(DoCode)}] Failed to perform code");
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
        /// <returns>HTTP status code: (200) File content (404) File not found (500) Generic error (502) Incompatible DCS version (503) DCS is unavailable</returns>
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
                    _logger.LogWarning($"[{nameof(DownloadFile)}] Could not find file {filename} (resolved to {resolvedPath})");
                    return NotFound(HttpUtility.UrlPathEncode(filename));
                }

                FileStream stream = new(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return File(stream, "application/octet-stream");
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException;
                }
                if (e is IncompatibleVersionException)
                {
                    _logger.LogError($"[{nameof(DownloadFile)}] Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    _logger.LogError($"[{nameof(DownloadFile)}] DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                _logger.LogWarning(e, $"[{nameof(DownloadFile)}] Failed download file {filename} (resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// PUT /machine/file/{filename}
        /// Upload a file from the HTTP body and create the subdirectories if necessary.
        /// </summary>
        /// <param name="filename">Destination of the file to upload</param>
        /// <returns>HTTP status code: (201) File created (500) Generic error occurred (502) Incompatible DCS version (503) DCS is unavailable</returns>
        [Authorize(Policy = Authorization.Policies.ReadWrite)]
        [DisableRequestSizeLimit]
        [HttpPut("file/{*filename}")]
        public async Task<IActionResult> UploadFile(string filename)
        {
            filename = HttpUtility.UrlDecode(filename);

            string resolvedPath = "n/a";
            try
            {
                Authorization.Sessions.SetLongRunningHttpRequest(HttpContext.User, true);
                try
                {
                    resolvedPath = await ResolvePath(filename);

                    // Create directory if necessary
                    string directory = Path.GetDirectoryName(resolvedPath);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // Write file
                    using (FileStream stream = new(resolvedPath, FileMode.Create, FileAccess.Write))
                    {
                        await Request.Body.CopyToAsync(stream);
                    }
                    return Created(HttpUtility.UrlPathEncode(filename), null);
                }
                finally
                {
                    Authorization.Sessions.SetLongRunningHttpRequest(HttpContext.User, false);
                }
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException;
                }
                if (e is IncompatibleVersionException)
                {
                    _logger.LogError($"[{nameof(UploadFile)}] Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    _logger.LogError($"[{nameof(UploadFile)}] DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                _logger.LogWarning(e, $"[{nameof(UploadFile)} Failed upload file {filename} (resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// GET /machine/fileinfo/{filename}
        /// Parse a given G-code file and return information about this job file as a JSON object.
        /// </summary>
        /// <param name="filename">G-code file to analyze</param>
        /// <returns>HTTP status code: (200) File info as application/json (404) File not found (500) Generic error (502) Incompatible DCS (503) DCS is unavailable</returns>
        [HttpGet("fileinfo/{*filename}")]
        public async Task<IActionResult> GetFileInfo(string filename)
        {
            filename = HttpUtility.UrlDecode(filename);

            string resolvedPath = "n/a";
            try
            {
                resolvedPath = await ResolvePath(filename);
                if (!System.IO.File.Exists(resolvedPath))
                {
                    _logger.LogWarning($"[{nameof(GetFileInfo)}] Could not find file {filename} (resolved to {resolvedPath})");
                    return NotFound(HttpUtility.UrlPathEncode(filename));
                }

                using CommandConnection connection = await BuildConnection();
                var info = await connection.GetFileInfo(resolvedPath);

                string json = JsonSerializer.Serialize(info, JsonHelper.DefaultJsonOptions);
                return Content(json, "application/json");
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException;
                }
                if (e is IncompatibleVersionException)
                {
                    _logger.LogError($"[{nameof(GetFileInfo)}] Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    _logger.LogError($"[{nameof(GetFileInfo)}] DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                _logger.LogWarning(e, $"[{nameof(GetFileInfo)}] Failed to retrieve file info for {filename} (resolved to {resolvedPath})");
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
        /// <returns>HTTP status code: (204) File or directory deleted (404) File not found (500) Generic error (502) Incompatible DCS version (503) DCS is unavailable</returns>
        [Authorize(Policy = Authorization.Policies.ReadWrite)]
        [HttpDelete("file/{*filename}")]
        public async Task<IActionResult> DeleteFileOrDirectory(string filename)
        {
            filename = HttpUtility.UrlDecode(filename);

            string resolvedPath = "n/a";
            try
            {
                resolvedPath = await ResolvePath(filename);

                if (Directory.Exists(resolvedPath))
                {
                    Directory.Delete(resolvedPath);
                    return NoContent();
                }

                if (System.IO.File.Exists(resolvedPath))
                {
                    System.IO.File.Delete(resolvedPath);
                    return NoContent();
                }

                _logger.LogWarning($"[{nameof(DeleteFileOrDirectory)} Could not find file {filename} (resolved to {resolvedPath})");
                return NotFound(HttpUtility.UrlPathEncode(filename));
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException;
                }
                if (e is IncompatibleVersionException)
                {
                    _logger.LogError($"[{nameof(DeleteFileOrDirectory)}] Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    _logger.LogError($"[{nameof(DeleteFileOrDirectory)}] DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                _logger.LogWarning(e, $"[{nameof(DeleteFileOrDirectory)} Failed to delete file {filename} (resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// Move a file or directory from a to b
        /// </summary>
        /// <param name="from">Source path</param>
        /// <param name="to">Destination path</param>
        /// <param name="force">Delete existing file (optional, default false)</param>
        /// <returns>HTTP status code: (204) File or directory moved (404) File or directory not found (500) Generic error (502) Incompatible DCS version (503) DCS is unavailable</returns>
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

                return NotFound(HttpUtility.UrlPathEncode(from));
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException;
                }
                if (e is IncompatibleVersionException)
                {
                    _logger.LogError($"[{nameof(MoveFileOrDirectory)}] Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    _logger.LogError($"[{nameof(MoveFileOrDirectory)}] DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                _logger.LogWarning(e, $"[{nameof(MoveFileOrDirectory)} Failed to move file {from} to {to} (resolved to {source} and {destination})");
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
        /// <returns>HTTP status code: (200) File list as application/json (404) Directory not found (500) Generic error (502) Incompatible DCS version (503) DCS is unavailable</returns>
        [HttpGet("directory/{*directory}")]
        public async Task<IActionResult> GetFileList(string directory)
        {
            directory = HttpUtility.UrlDecode(directory);

            string resolvedPath = "n/a";
            try
            {
                resolvedPath = await ResolvePath(directory);
                if (!Directory.Exists(resolvedPath))
                {
                    _logger.LogWarning($"[{nameof(GetFileList)}] Could not find directory {directory} (resolved to {resolvedPath})");
                    return NotFound(HttpUtility.UrlPathEncode(directory));
                }
                return Content(FileLists.GetFileList(directory, resolvedPath), "application/json");
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException;
                }
                if (e is IncompatibleVersionException)
                {
                    _logger.LogError($"[{nameof(GetFileList)}] Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    _logger.LogError($"[{nameof(GetFileList)}] DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                _logger.LogWarning(e, $"[{nameof(GetFileList)}] Failed to retrieve file list for {directory} (resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// PUT /machine/directory/{directory}
        /// Create the given directory.
        /// </summary>
        /// <param name="directory">Directory to create</param>
        /// <returns>HTTP status code: (204) Directory created (500) Generic error (502) Incompatible DCS version (503) DCS is unavailable</returns>
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
                    e = ae.InnerException;
                }
                if (e is IncompatibleVersionException)
                {
                    _logger.LogError($"[{nameof(CreateDirectory)}] Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    _logger.LogError($"[{nameof(CreateDirectory)}] DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                _logger.LogWarning(e, $"[{nameof(CreateDirectory)}] Failed to create directory {directory} (resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            }
        }
        #endregion

        #region Plugins
        /// <summary>
        /// PUT /machine/plugin
        /// Install or upgrade a plugin ZIP file
        /// </summary>
        /// <returns>HTTP status code: (204) No content (500) Generic error occurred (502) Incompatible DCS version (503) DCS is unavailable</returns>
        [Authorize(Policy = Authorization.Policies.ReadWrite)]
        [DisableRequestSizeLimit]
        [HttpPut("plugin")]
        public async Task<IActionResult> InstallPlugin()
        {
            string zipFile = Path.GetTempFileName();
            try
            {
                Authorization.Sessions.SetLongRunningHttpRequest(HttpContext.User, true);
                try
                {
                    // Write ZIP file
                    using (FileStream stream = new(zipFile, FileMode.Create, FileAccess.Write, FileShare.None))
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
                        e = ae.InnerException;
                    }
                    if (e is IncompatibleVersionException)
                    {
                        _logger.LogError($"[{nameof(InstallPlugin)}] Incompatible DCS version");
                        return StatusCode(502, "Incompatible DCS version");
                    }
                    if (e is SocketException)
                    {
                        _logger.LogError($"[{nameof(InstallPlugin)}] DCS is not started");
                        return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                    }
                    _logger.LogWarning(e, $"[{nameof(InstallPlugin)} Failed to upload ZIP file to {zipFile}");
                    return StatusCode(500, e.Message);
                }
            }
            finally
            {
                Authorization.Sessions.SetLongRunningHttpRequest(HttpContext.User, false);
                System.IO.File.Delete(zipFile);
            }
        }

        /// <summary>
        /// DELETE /machine/plugin
        /// Uninstall a plugin
        /// </summary>
        /// <returns>HTTP status code: (204) No content (500) Generic error occurred (502) Incompatible DCS version (503) DCS is unavailable</returns>
        [Authorize(Policy = Authorization.Policies.ReadWrite)]
        [HttpDelete("plugin")]
        public async Task<IActionResult> UninstallPlugin()
        {
            try
            {
                Authorization.Sessions.SetLongRunningHttpRequest(HttpContext.User, true);
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
                    Authorization.Sessions.SetLongRunningHttpRequest(HttpContext.User, false);
                }
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException;
                }
                if (e is IncompatibleVersionException)
                {
                    _logger.LogError($"[{nameof(UninstallPlugin)}] Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    _logger.LogError($"[{nameof(UninstallPlugin)}] DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                _logger.LogWarning(e, $"[{nameof(UninstallPlugin)} Failed to uninstall plugin");
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
            public string plugin { get; set; }

            /// <summary>
            /// Key to change
            /// </summary>
            public string key { get; set; }

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
        /// <returns>HTTP status code: (204) No content (500) Generic error occurred (502) Incompatible DCS version (503) DCS is unavailable</returns>
        [Authorize(Policy = Authorization.Policies.ReadWrite)]
        [DisableRequestSizeLimit]
        [HttpPatch("plugin")]
        public async Task<IActionResult> SetPluginData()
        {
            try
            {
                PluginPatchInstruction instruction = await JsonSerializer.DeserializeAsync<PluginPatchInstruction>(HttpContext.Request.Body);

                using CommandConnection connection = await BuildConnection();
                ObjectModel model = await connection.GetObjectModel();
                if (model.Plugins.TryGetValue(instruction.plugin, out Plugin plugin))
                {
                    if (!string.IsNullOrEmpty(plugin.SbcExecutable))
                    {
                        _logger.LogWarning("Tried to set plugin data for {0} but it has an SBC executable set");
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
                    e = ae.InnerException;
                }
                if (e is IncompatibleVersionException)
                {
                    _logger.LogError($"[{nameof(SetPluginData)}] Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    _logger.LogError($"[{nameof(SetPluginData)}] DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                _logger.LogWarning(e, $"[{nameof(SetPluginData)} Failed to set plugin data");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// POST /machine/startPlugin
        /// Start a plugin on the SBC
        /// </summary>
        /// <returns>HTTP status code: (204) No content (500) Generic error occurred (502) Incompatible DCS version (503) DCS is unavailable</returns>
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
                    e = ae.InnerException;
                }
                if (e is IncompatibleVersionException)
                {
                    _logger.LogError($"[{nameof(StartPlugin)}] Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    _logger.LogError($"[{nameof(StartPlugin)}] DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                _logger.LogWarning(e, $"[{nameof(StartPlugin)} Failed to start plugin");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// POST /machine/stopPlugin
        /// Stop a plugin on the SBC
        /// </summary>
        /// <returns>HTTP status code: (204) No content (500) Generic error occurred (502) Incompatible DCS version (503) DCS is unavailable</returns>
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
                    e = ae.InnerException;
                }
                if (e is IncompatibleVersionException)
                {
                    _logger.LogError($"[{nameof(StopPlugin)}] Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    _logger.LogError($"[{nameof(StopPlugin)}] DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                _logger.LogWarning(e, $"[{nameof(StopPlugin)} Failed to stop plugin");
                return StatusCode(500, e.Message);
            }
        }
        #endregion

        #region System packages
        /// <summary>
        /// PUT /machine/systemPackage
        /// Install or upgrade a system package
        /// </summary>
        /// <returns>HTTP status code: (204) No content (500) Generic error occurred (502) Incompatible DCS version (503) DCS is unavailable</returns>
        [Authorize(Policy = Authorization.Policies.ReadWrite)]
        [DisableRequestSizeLimit]
        [HttpPut("systemPackage")]
        public async Task<IActionResult> InstallSystemPackage()
        {
            string packageFile = Path.GetTempFileName();
            try
            {
                Authorization.Sessions.SetLongRunningHttpRequest(HttpContext.User, true);
                try
                {
                    // Write package file
                    using (FileStream stream = new(packageFile, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await Request.Body.CopyToAsync(stream);
                    }

                    // Install it
                    using CommandConnection connection = await BuildConnection();
                    await connection.InstallSystemPackage(packageFile);

                    return NoContent();
                }
                catch (Exception e)
                {
                    if (e is AggregateException ae)
                    {
                        e = ae.InnerException;
                    }
                    if (e is IncompatibleVersionException)
                    {
                        _logger.LogError($"[{nameof(InstallSystemPackage)}] Incompatible DCS version");
                        return StatusCode(502, "Incompatible DCS version");
                    }
                    if (e is SocketException)
                    {
                        _logger.LogError($"[{nameof(InstallSystemPackage)}] DCS is not started");
                        return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                    }
                    _logger.LogWarning(e, $"[{nameof(InstallSystemPackage)} Failed to upload package file to {packageFile}");
                    return StatusCode(500, e.Message);
                }
            }
            finally
            {
                Authorization.Sessions.SetLongRunningHttpRequest(HttpContext.User, false);
                System.IO.File.Delete(packageFile);
            }
        }

        /// <summary>
        /// DELETE /machine/systemPackage
        /// Uninstall a system package
        /// </summary>
        /// <returns>HTTP status code: (204) No content (500) Generic error occurred (502) Incompatible DCS version (503) DCS is unavailable</returns>
        [Authorize(Policy = Authorization.Policies.ReadWrite)]
        [HttpDelete("systemPackage")]
        public async Task<IActionResult> UninstallSystemPackage()
        {
            try
            {
                Authorization.Sessions.SetLongRunningHttpRequest(HttpContext.User, true);
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
                    Authorization.Sessions.SetLongRunningHttpRequest(HttpContext.User, false);
                }
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException;
                }
                if (e is IncompatibleVersionException)
                {
                    _logger.LogError($"[{nameof(UninstallPlugin)}] Incompatible DCS version");
                    return StatusCode(502, "Incompatible DCS version");
                }
                if (e is SocketException)
                {
                    _logger.LogError($"[{nameof(UninstallPlugin)}] DCS is not started");
                    return StatusCode(503, "Failed to connect to Duet, please check your connection (DCS is not started)");
                }
                _logger.LogWarning(e, $"[{nameof(UninstallPlugin)} Failed to uninstall system package");
                return StatusCode(500, e.Message);
            }
        }
        #endregion

        private async Task<CommandConnection> BuildConnection()
        {
            CommandConnection connection = new();
            await connection.Connect(_configuration.GetValue("SocketPath", Defaults.FullSocketPath));
            return connection;
        }

        private async Task<string> ResolvePath(string path)
        {
            using CommandConnection connection = await BuildConnection();
            return await connection.ResolvePath(path);
        }
    }
}
