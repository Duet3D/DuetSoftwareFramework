using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace DuetRestEndpoint.Controllers
{
    /// <summary>
    /// Model View Controller for /machine requests
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class MachineController : ControllerBase
    {
        private readonly IConfiguration _configuration;
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

        /// <summary>
        /// WS /machine
        /// Provide WebSocket for continuous model updates. This is priarily used to keep DWC2 up-to-date
        /// </summary>
        /// <returns>WebSocket, HTTP status code: (400) Bad request, WebSocket code: (1001) DCS unavailable</returns>
        [HttpGet]
        public async Task Get()
        {
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                WebSocket webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
                if (Services.ModelProvider.IsConnected)
                {
                    await WebSocketController.Process(webSocket, _logger);
                }
                else
                {
                    _logger.LogError($"[{nameof(Get)}] DCS unavailable");
                    await webSocket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "DCS unavailable", default(CancellationToken));
                }
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
            }
        }

        /// <summary>
        /// GET /machine/status
        /// Retrieve the full object model as JSON.
        /// </summary>
        /// <returns>Machine object model as JSON text or HTTP status code: (503) Machine model unavailable</returns>
        [HttpGet("status")]
        public IActionResult Status()
        {
            if (Services.ModelProvider.IsConnected)
            {
                string json = JsonConvert.SerializeObject(Services.ModelProvider.GetFull(), DuetAPI.JsonHelper.DefaultSettings);
                return Content(json, "application/json");
            }

            _logger.LogError($"[{nameof(Status)}] DCS unavailable");
            return StatusCode(503, "DCS unavailable");
        }

        /// <summary>
        /// POST machine/code
        /// Execute a G/M/T-code and return the G-code response when done.
        /// </summary>
        /// <param name="code">G/M/T-code to execute</param>
        /// <returns>G-Code response or HTTP status code: (500) Generic error occurred (502) Incompatible DCS version (503) DCS unavailable</returns>
        [HttpPost]
        public async Task<IActionResult> DoCode([FromBody] string code)
        {
            try
            {
                using (DuetAPIClient.Connection connection = await BuildConnection())
                {
                    string response = await connection.PerformSimpleCode(code);
                    return Content(response);
                }
            }
            catch (InvalidOperationException e)
            {
                _logger.LogError($"[{nameof(DoCode)}] Incompatible DCS version");
                return StatusCode(502, e.Message);
            }
            catch (IOException e)
            {
                _logger.LogError($"[{nameof(DoCode)}] DCS unavailable");
                return StatusCode(503, e.Message);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"[{nameof(DoCode)}] Failed to execute code {code}");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// GET machine/files/{filename}
        /// Download the specified file.
        /// </summary>
        /// <param name="filename">File to download</param>
        /// <returns>File content or HTTP status code: (404) File not found (500) Generic error (502) Incompatible DCS (503) DCS unavailable</returns>
        [HttpGet("files/{filename}")]
        public async Task<IActionResult> DownloadFile(string filename)
        {
            string resolvedPath = "n/a";
            try
            {
                resolvedPath = await ResolvePath(filename);
                if (System.IO.File.Exists(resolvedPath))
                {
                    _logger.LogWarning($"{nameof(DownloadFile)}] Could not find file {filename} (resolved to {resolvedPath})");
                    return NotFound(filename);
                }

                FileStream stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return File(stream, "application/octet-stream");
            }
            catch (InvalidOperationException e)
            {
                _logger.LogError($"[{nameof(DownloadFile)}] Incompatible DCS version");
                return StatusCode(502, e.Message);
            }
            catch (IOException e)
            {
                _logger.LogError($"[{nameof(DownloadFile)}] DCS unavailable");
                return StatusCode(503, e.Message);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"[{nameof(DownloadFile)}] Failed download file {filename} (resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// POST machine/files/{filename}
        /// Upload a file from the HTTP body and create the subdirectories if necessary.
        /// </summary>
        /// <param name="filename">Destination of the file to upload</param>
        /// <returns>HTTP status code: (204) File created (500) Generic error occurred (502) Incompatible DCS (503) DCS unavailable</returns>
        [HttpPost("files/{filename}")]
        public async Task<IActionResult> UploadFile(string filename)
        {
            string resolvedPath = "n/a";
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
                using (FileStream stream = new FileStream(resolvedPath, FileMode.Create, FileAccess.Write))
                {
                    await Request.Body.CopyToAsync(stream);
                }
                return Created(filename, null);
            }
            catch (InvalidOperationException e)
            {
                _logger.LogError($"[{nameof(UploadFile)}] Incompatible DCS version");
                return StatusCode(502, e.Message);
            }
            catch (IOException e)
            {
                _logger.LogError($"[{nameof(UploadFile)}] DCS unavailable");
                return StatusCode(503, e.Message);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"[{nameof(UploadFile)} Failed upload file {filename} ({Request.Body.Length} bytes, resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// DELETE machine/files/{filename}
        /// Delete the given file or directory.
        /// </summary>
        /// <param name="filename">File or directory to delete</param>
        /// <returns>HTTP status code: (204) File or directory deleted (404) File not found (500) Generic error (502) Incompatible DCS (503) DCS unavailable</returns>
        [HttpDelete("files/{filename}")]
        public async Task<IActionResult> DeleteFile(string filename)
        {
            string resolvedPath = "n/a";
            try
            {
                resolvedPath = await ResolvePath(filename);

                if (Directory.Exists(resolvedPath))
                {
                    Directory.Delete(resolvedPath);
                    return new EmptyResult();
                }

                if (System.IO.File.Exists(resolvedPath))
                {
                    System.IO.File.Delete(resolvedPath);
                    return new EmptyResult();
                }

                _logger.LogWarning($"[{nameof(DeleteFile)} Could not find file {filename} (resolved to {resolvedPath})");
                return NotFound(filename);
            }
            catch (InvalidOperationException e)
            {
                _logger.LogError($"[{nameof(DeleteFile)}] Incompatible DCS version");
                return StatusCode(502, e.Message);
            }
            catch (IOException e)
            {
                _logger.LogError($"[{nameof(DeleteFile)}] DCS unavailable");
                return StatusCode(503, e.Message);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"[{nameof(DeleteFile)} Failed delete file {filename} (resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// GET fileinfo/{filename}
        /// Retrieve file info for the specified file
        /// </summary>
        /// <param name="filename">G-code file to analyze</param>
        /// <returns>File info as JSON or HTTP status code: (404) File not found (500) Generic error (502) Incompatible DCS (503) DCS unavailable</returns>
        [HttpGet("fileinfo/{filename}")]
        public async Task<IActionResult> GetFileinfo(string filename)
        {
            string resolvedPath = "n/a";
            try
            {
                resolvedPath = await ResolvePath(filename);
                if (!System.IO.File.Exists(resolvedPath))
                {
                    _logger.LogWarning($"[{nameof(GetFileinfo)}] Could not find file {filename} (resolved to {resolvedPath})");
                    return NotFound(filename);
                }

                using (DuetAPIClient.Connection connection = await BuildConnection())
                {
                    var info = await connection.GetFileInfo(resolvedPath);
                    string json = JsonConvert.SerializeObject(info, DuetAPI.JsonHelper.DefaultSettings);
                    return Content(info.ToString());
                }
            }
            catch (InvalidOperationException e)
            {
                _logger.LogError($"[{nameof(GetFileinfo)}] Incompatible DCS version");
                return StatusCode(502, e.Message);
            }
            catch (IOException e)
            {
                _logger.LogError($"[{nameof(GetFileinfo)}] DCS unavailable");
                return StatusCode(503, e.Message);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"[{nameof(GetFileinfo)} Failed to retrieve file info for {filename} (resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            }
        }

        private async Task<DuetAPIClient.Connection> BuildConnection()
        {
            DuetAPIClient.Connection connection = new DuetAPIClient.Connection(DuetAPI.Connection.ConnectionType.Command);
            await connection.Connect(_configuration.GetValue("SocketPath", "/tmp/duet.sock"));
            return connection;
        }

        private async Task<string> ResolvePath(string path)
        {
            using (DuetAPIClient.Connection connection = await BuildConnection())
            {
                return await connection.ResolvePath(path);
            }
        }
    }
}
