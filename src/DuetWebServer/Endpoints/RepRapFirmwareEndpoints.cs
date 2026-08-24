using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Web;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetAPIClient;
using DuetWebServer.Singletons;
using DuetWebServer.Utility;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DuetWebServer.Endpoints;

/// <summary>
/// Minimal-API endpoints for /rr_ requests
/// </summary>
public class RepRapFirmwareEndpoints
{
    /// <summary>
    /// Register the /rr_ endpoints
    /// </summary>
    /// <param name="app">Web application</param>
    public static void Map(WebApplication app)
    {
        RouteGroupBuilder rrf = app.MapGroup("").RequireAuthorization(Authorization.Policies.ReadOnly);

        rrf.MapGet("/rr_connect", Connect).AllowAnonymous();
        rrf.MapGet("/rr_disconnect", Disconnect).AllowAnonymous();
        rrf.MapGet("/rr_gcode", DoCode).RequireAuthorization(Authorization.Policies.ReadWrite);
        rrf.MapGet("/rr_reply", Reply);
        rrf.MapGet("/rr_upload", UploadResult);
        rrf.MapPost("/rr_upload", UploadFile).RequireAuthorization(Authorization.Policies.ReadWrite);
        rrf.MapGet("/rr_download", DownloadFile);
        rrf.MapGet("/rr_delete", DeleteFileOrDirectory).RequireAuthorization(Authorization.Policies.ReadWrite);
        rrf.MapGet("/rr_filelist", GetFileList);
        rrf.MapGet("/rr_files", GetFiles);
        rrf.MapGet("/rr_model", GetModel);
        rrf.MapGet("/rr_move", MoveFileOrDirectory).RequireAuthorization(Authorization.Policies.ReadWrite);
        rrf.MapGet("/rr_mkdir", CreateDirectory).RequireAuthorization(Authorization.Policies.ReadWrite);
        rrf.MapGet("/rr_fileinfo", GetFileInfo);
        rrf.MapGet("/rr_thumbnail", GetThumbnail);
    }

    /// <summary>
    /// Serialize a JSON node using the relaxed encoder to match the object model wire format
    /// </summary>
    /// <param name="node">Node to serialize</param>
    /// <returns>Serialized JSON</returns>
    private static string SerializeNodeRelaxed(JsonNode node)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            node.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// GET /rr_connect?password={password}
    /// Attempt to create a new connection and log in using the (optional) password
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="sessionStorage">Session storage singleton</param>
    /// <param name="password">Password to check</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> Connect(HttpContext context, ILogger<RepRapFirmwareEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, ISessionStorage sessionStorage, string? password)
    {
        Settings settings = settingsMonitor.CurrentValue;
        try
        {
            using CommandConnection connection = await EndpointHelper.BuildConnectionAsync(settings.SocketPath);
            if ((settings.OverrideWebPassword is null && await connection.CheckPasswordAsync(password ?? string.Empty)) ||
                (settings.OverrideWebPassword is not null && settings.OverrideWebPassword == (password ?? string.Empty)))
            {
                int sessionId = await connection.AddUserSessionAsync(AccessLevel.ReadWrite, SessionType.HTTP, context.Connection.RemoteIpAddress!.ToString());
                _ = sessionStorage.MakeSessionKey(sessionId, context.Connection.RemoteIpAddress.ToString(), true);

                // See RepRapFirmware/src/Platform/Platform.cpp -> Platform::GetBoardString()
                ObjectModel model = await connection.GetObjectModelAsync();
                string boardString = model.Boards.First(board => board.CanAddress is null or 0)?.ShortName switch
                {
                    "Mini5plus" => "duet5lcunknown",
                    "MB6HC" => "duet3mb6hc100",
                    "MB6XD" => "duet3mb6xd100",
                    "FMDC" => "fmdc",
                    "2WiFi" => "duetwifi10",
                    "2Ethernet" => "duetethernet10",
                    "2SBC" => "duet2sbc10",
                    "2Maestro" => "duetmaestro100",
                    "PC001373" => "pc001373",
                    _ => "unknown"
                };

                return Results.Text(JsonSerializer.Serialize(new RepRapFirmwareConnectResponse
                {
                    ApiLevel = 1,
                    Err = 0,
                    IsEmulated = true,
                    SessionTimeout = 8000,
                    BoardType = boardString
                }, DwsJsonContext.Default.RepRapFirmwareConnectResponse), "application/json");
            }
            else
            {
                EndpointHelper.LogWarning(logger, "Invalid password");
                return Results.Text("{\"err\":1,\"isEmulated\":true}", "application/json");
            }
        }
        catch (Exception e)
        {
            return await EndpointHelper.HandleDcsExceptionAsync(e, logger, settings, "Failed to handle connect request");
        }
    }

    /// <summary>
    /// GET /rr_disconnect
    /// Disconnect again from the RepRapFirmware controller
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="sessionStorage">Session storage singleton</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> Disconnect(HttpContext context, ILogger<RepRapFirmwareEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, ISessionStorage sessionStorage)
    {
        Settings settings = settingsMonitor.CurrentValue;
        try
        {
            if (context.User is not null)
            {
                // Remove the internal session
                int sessionId = sessionStorage.RemoveTicket(context.User);

                // Remove the DSF user session again
                if (sessionId > 0)
                {
                    using CommandConnection connection = await EndpointHelper.BuildConnectionAsync(settings.SocketPath);
                    await connection.RemoveUserSessionAsync(sessionId);
                }
            }
            return Results.NoContent();
        }
        catch (Exception e)
        {
            EndpointHelper.LogError(logger, e, "Failed to handle rr_disconnect request");
        }
        return Results.Text("{\"err\":1}", "application/json");
    }

    /// <summary>
    /// GET /rr_gcode?gcode={gcode}
    /// Execute plain G/M/T-code(s) and return an acknowledgement
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="gcode">G-code(s) to execute</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> DoCode(ILogger<RepRapFirmwareEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, string? gcode)
    {
        Settings settings = settingsMonitor.CurrentValue;
        try
        {
            if (!string.IsNullOrWhiteSpace(gcode))
            {
                using CommandConnection connection = await EndpointHelper.BuildConnectionAsync(settings.SocketPath);
                EndpointHelper.LogInformation(logger, $"Executing code '{gcode}'");
                _ = await connection.PerformSimpleCodeAsync(gcode, CodeChannel.HTTP, true);
            }
            return Results.Text("{\"bufferSpace\":255,\"err\":0}", "application/json");
        }
        catch (Exception e)
        {
            EndpointHelper.LogError(logger, e, "Failed to handle rr_gcode request");
        }
        return Results.Text("{\"err\":1}", "application/json");
    }

    /// <summary>
    /// GET /rr_reply
    /// Retrieve the last G-code reply
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="sessionStorage">Session storage singleton</param>
    /// <returns>HTTP result</returns>
    private static IResult Reply(HttpContext context, ILogger<RepRapFirmwareEndpoints> logger, ISessionStorage sessionStorage)
    {
        try
        {
            string reply = sessionStorage.GetCachedMessages(context.User);
            return Results.Text(reply, "text/plain");
        }
        catch (Exception e)
        {
            EndpointHelper.LogError(logger, e, "Failed to handle rr_reply request");
        }
        return Results.Text("{\"err\":1}", "application/json");
    }

    /// <summary>
    /// Indicates if the last upload was successful. Static because GET /rr_upload queries the result of a previous POST request
    /// </summary>
    private static volatile bool _lastUploadSuccessful = true;

    /// <summary>
    /// GET /rr_upload
    /// Get the last file upload result
    /// </summary>
    /// <returns>HTTP result</returns>
    private static IResult UploadResult() => Results.Text("{\"err\":" + (_lastUploadSuccessful ? '0' : '1') + "}", "application/json");

    /// <summary>
    /// POST /rr_upload?name={filename}
    /// Upload a file from the HTTP body and create the subdirectories if necessary
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="sessionStorage">Session storage singleton</param>
    /// <param name="name">Destination of the file to upload</param>
    /// <param name="time">Last modified time of the file</param>
    /// <param name="crc32">CRC32 checksum of the file</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> UploadFile(HttpContext context, ILogger<RepRapFirmwareEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, ISessionStorage sessionStorage, string? name, string? time, string? crc32)
    {
        Settings settings = settingsMonitor.CurrentValue;

        EndpointHelper.DisableRequestSizeLimit(context);
        try
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                sessionStorage.SetLongRunningHttpRequest(context.User, true);
                try
                {
                    string resolvedPath = await EndpointHelper.ResolvePathAsync(settings.SocketPath, name);

                    // Create directory if necessary
                    string directory = Path.GetDirectoryName(resolvedPath)!;
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    if (string.IsNullOrEmpty(crc32))
                    {
                        // Write plain file
                        await using FileStream stream = new(resolvedPath, FileMode.Create, FileAccess.ReadWrite);
                        await context.Request.Body.CopyToAsync(stream);
                    }
                    else
                    {
                        uint computedCrc32 = 0;
                        await using (FileStream stream = new(resolvedPath, FileMode.Create, FileAccess.ReadWrite))
                        {
                            // Write file
                            await context.Request.Body.CopyToAsync(stream);

                            // Compute CRC32
                            if (!string.IsNullOrEmpty(crc32))
                            {
                                stream.Seek(0, SeekOrigin.Begin);
                                computedCrc32 = await CRC32.Calculate(stream);
                            }
                        }

                        // Verify CRC32 checksum
                        if (!computedCrc32.ToString("x8").Equals(crc32, StringComparison.InvariantCultureIgnoreCase))
                        {
                            EndpointHelper.LogWarning(logger, $"CRC32 check failed in rr_upload ({crc32} != {computedCrc32:x8})");
                            _lastUploadSuccessful = false;
                            File.Delete(resolvedPath);
                            return Results.Text("{\"err\":1}", "application/json");
                        }
                    }

                    // Set last modified time if applicable
                    if (!string.IsNullOrEmpty(time) && DateTime.TryParse(time, out DateTime lastModified))
                    {
                        File.SetLastWriteTime(resolvedPath, lastModified);
                    }

                    _lastUploadSuccessful = true;
                    return Results.Text("{\"err\":0}", "application/json");
                }
                finally
                {
                    sessionStorage.SetLongRunningHttpRequest(context.User, false);
                }
            }
        }
        catch (Exception e)
        {
            _lastUploadSuccessful = false;
            EndpointHelper.LogError(logger, e, "Failed to handle rr_upload request");
        }
        return Results.Text("{\"err\":1}", "application/json");
    }

    /// <summary>
    /// GET /rr_download?name={filename}
    /// Download the specified file
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="name">File to download</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> DownloadFile(ILogger<RepRapFirmwareEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, string? name)
    {
        Settings settings = settingsMonitor.CurrentValue;
        try
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                string resolvedPath = await EndpointHelper.ResolvePathAsync(settings.SocketPath, name);
                if (!File.Exists(resolvedPath))
                {
                    EndpointHelper.LogWarning(logger, $"Could not find file {name} (resolved to {resolvedPath})");
                    return Results.Text(HttpUtility.UrlPathEncode(name), statusCode: StatusCodes.Status404NotFound);
                }

                FileStream stream = new(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return Results.Stream(stream, "application/octet-stream");
            }
        }
        catch (Exception e)
        {
            EndpointHelper.LogError(logger, e, "Failed to handle rr_download request");
        }
        return Results.Text("{\"err\":1}", "application/json");
    }

    /// <summary>
    /// GET /rr_delete?name={filename}
    /// Delete the given file or directory
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="name">File or directory to delete</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> DeleteFileOrDirectory(ILogger<RepRapFirmwareEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, string? name)
    {
        Settings settings = settingsMonitor.CurrentValue;
        try
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                string resolvedPath = await EndpointHelper.ResolvePathAsync(settings.SocketPath, name);

                if (Directory.Exists(resolvedPath))
                {
                    Directory.Delete(resolvedPath);
                    return Results.Text("{\"err\":0}", "application/json");
                }

                if (File.Exists(resolvedPath))
                {
                    File.Delete(resolvedPath);
                    return Results.Text("{\"err\":0}", "application/json");
                }

                EndpointHelper.LogWarning(logger, $"Could not find file {name} (resolved to {resolvedPath})");
            }
        }
        catch (Exception e)
        {
            EndpointHelper.LogError(logger, e, "Failed to handle rr_delete request");
        }
        return Results.Text("{\"err\":1}", "application/json");
    }

    /// <summary>
    /// GET /rr_filelist?dir={directory}&amp;first={first}
    /// Retrieve a file list
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="dir">Directory to list</param>
    /// <param name="first">First file to list or -1 if unknown</param>
    /// <param name="max">Maximum number of files to list or -1 if unset</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> GetFileList(ILogger<RepRapFirmwareEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, string? dir, int first = 0, int max = -1)
    {
        Settings settings = settingsMonitor.CurrentValue;
        try
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                return Results.Text("{\"err\":1}");
            }
            string resolvedPath = await EndpointHelper.ResolvePathAsync(settings.SocketPath, dir);
            return Results.Bytes(FileLists.GetFileListUtf8(dir, resolvedPath, Math.Max(first, 0), -1, max), "application/json");
        }
        catch (Exception e)
        {
            EndpointHelper.LogError(logger, e, "Failed to handle rr_filelist request");
        }
        return Results.Text("{\"err\":2}", "application/json");
    }

    /// <summary>
    /// GET /rr_files?dir={directory}&amp;first={first}&amp;flagDirs={flagDirs}
    /// Retrieve a files list
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="dir">Directory to list</param>
    /// <param name="first">First file to list (defaults to 0)</param>
    /// <param name="flagDirs">Whether directories should be flagged using an asterisk prefix</param>
    /// <param name="max">Maximum number of files to list or -1 if unset</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> GetFiles(ILogger<RepRapFirmwareEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, string? dir, int first = 0, int flagDirs = 0, int max = -1)
    {
        Settings settings = settingsMonitor.CurrentValue;
        try
        {
            if (!string.IsNullOrWhiteSpace(dir))
            {
                string resolvedPath = await EndpointHelper.ResolvePathAsync(settings.SocketPath, dir);
                return Results.Text(FileLists.GetFiles(dir, resolvedPath, Math.Max(first, 0), flagDirs != 0, -1, max), "application/json");
            }
        }
        catch (Exception e)
        {
            EndpointHelper.LogError(logger, e, "Failed to handle rr_filelist request");
        }
        return Results.Text("{\"err\":1}", "application/json");
    }

    /// <summary>
    /// GET /rr_model?key={key}&amp;flags={flags}
    /// Retrieve object model information
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="modelProvider">Model provider singleton</param>
    /// <param name="key">Object model key to query</param>
    /// <param name="flags">Query flags</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> GetModel(ILogger<RepRapFirmwareEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, IModelProvider modelProvider, string? key = "", string? flags = "")
    {
        Settings settings = settingsMonitor.CurrentValue;
        try
        {
            // Check key and flags for valid chars
            if (key is not null)
            {
                foreach (char c in key)
                {
                    if (!char.IsLetterOrDigit(c) && c != '.' && c != '[' && c != ']')
                    {
                        EndpointHelper.LogWarning(logger, $"Invalid character in rr_model key parameter: '{c}'");
                        return Results.Text("{\"err\":1}", "application/json");
                    }
                }
            }
            if (flags is not null)
            {
                foreach (char c in flags)
                {
                    if (!char.IsLetterOrDigit(c))
                    {
                        EndpointHelper.LogWarning(logger, $"Invalid character in rr_model flags parameter: '{c}'");
                        return Results.Text("{\"err\":1}", "application/json");
                    }
                }
            }

            using CommandConnection connection = await EndpointHelper.BuildConnectionAsync(settings.SocketPath);

            if (string.IsNullOrWhiteSpace(key) && flags?.Contains('f') == true)
            {
                // Live query with sequence numbers
                JsonElement response = await connection.QueryObjectModelAsync(key ?? string.Empty, flags);

                // Patch seqs.reply with DWS-managed reply sequence number
                if (response.TryGetProperty("result", out JsonElement resultElement) &&
                    resultElement.TryGetProperty("seqs", out _))
                {
                    if (JsonNode.Parse(response.GetRawText()) is JsonObject rootObject &&
                        rootObject["result"] is JsonObject resultObject &&
                        resultObject["seqs"] is JsonObject seqsObject)
                    {
                        lock (modelProvider)
                        {
                            seqsObject["reply"] = modelProvider.ReplySeq;
                        }
                        return Results.Text(SerializeNodeRelaxed(rootObject), "application/json");
                    }
                }

                return Results.Text(response.GetRawText(), "application/json");
            }
            else
            {
                // Standard query
                JsonElement response = await connection.QueryObjectModelAsync(key ?? string.Empty, flags ?? string.Empty);
                return Results.Text(response.GetRawText(), "application/json");
            }
        }
        catch (Exception e)
        {
            EndpointHelper.LogError(logger, e, "Failed to handle rr_model request");
        }
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// GET /rr_move?old={old}&amp;new={new}&amp;deleteexisting={deleteexisting}
    /// Move a file or directory from a to b
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="old">Source path</param>
    /// <param name="new">Destination path</param>
    /// <param name="deleteexisting">Delete existing file (optional, default "no")</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> MoveFileOrDirectory(ILogger<RepRapFirmwareEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, string? old, string? @new, string? deleteexisting = "no")
    {
        Settings settings = settingsMonitor.CurrentValue;
        try
        {
            string source = await EndpointHelper.ResolvePathAsync(settings.SocketPath, old ?? string.Empty);
            string destination = await EndpointHelper.ResolvePathAsync(settings.SocketPath, @new ?? string.Empty);

            // Deal with directories
            if (Directory.Exists(source))
            {
                if (Directory.Exists(destination))
                {
                    if (deleteexisting == "yes")
                    {
                        Directory.Delete(destination);
                    }
                    else
                    {
                        EndpointHelper.LogWarning(logger, $"Directory {old} already exists");
                        return Results.Text("{\"err\":1}", "application/json");
                    }
                }

                Directory.Move(source, destination);
                return Results.Text("{\"err\":0}", "application/json");
            }

            // Deal with files
            if (File.Exists(source))
            {
                if (File.Exists(destination))
                {
                    if (deleteexisting == "yes")
                    {
                        File.Delete(destination);
                    }
                    else
                    {
                        EndpointHelper.LogWarning(logger, $"File {old} already exists");
                        return Results.Text("{\"err\":1}", "application/json");
                    }
                }

                File.Move(source, destination);
                return Results.Text("{\"err\":0}", "application/json");
            }

            EndpointHelper.LogWarning(logger, $"File or directory {old} not found in rr_move");
        }
        catch (Exception e)
        {
            EndpointHelper.LogError(logger, e, "Failed to handle rr_move request");
        }
        return Results.Text("{\"err\":1}", "application/json");
    }

    /// <summary>
    /// GET /rr_mkdir?dir={dir}
    /// Create the given directory
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="dir">Directory to create</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> CreateDirectory(ILogger<RepRapFirmwareEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, string? dir)
    {
        Settings settings = settingsMonitor.CurrentValue;
        try
        {
            if (!string.IsNullOrWhiteSpace(dir))
            {
                string resolvedPath = await EndpointHelper.ResolvePathAsync(settings.SocketPath, dir);
                Directory.CreateDirectory(resolvedPath);
                return Results.Text("{\"err\":0}", "application/json");
            }
        }
        catch (Exception e)
        {
            EndpointHelper.LogError(logger, e, "Failed to handle rr_mkdir request");
        }
        return Results.Text("{\"err\":1}", "application/json");
    }

    /// <summary>
    /// Last queried file info
    /// </summary>
    private static GCodeFileInfo? _lastFileInfo;

    /// <summary>
    /// Lock for the cached file info
    /// </summary>
    private static readonly object _lastFileInfoLock = new();

    /// <summary>
    /// GET /rr_fileinfo?name={filename}
    /// Parse a given G-code file and return information about this job file as a JSON object
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="name">Optional G-code file to analyze</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> GetFileInfo(ILogger<RepRapFirmwareEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, string? name)
    {
        Settings settings = settingsMonitor.CurrentValue;
        try
        {
            // Filename defaults to the file being printed if it is not present
            int? printDuration = null;
            using CommandConnection connection = await EndpointHelper.BuildConnectionAsync(settings.SocketPath);
            if (string.IsNullOrEmpty(name))
            {
                ObjectModel model = await connection.GetObjectModelAsync();
                if (string.IsNullOrEmpty(model.Job.File.FileName))
                {
                    // Not printing a file, cannot get fileinfo
                    return Results.Text("{\"err\":1}", "application/json");
                }
                name = model.Job.File.FileName;
                printDuration = model.Job.Duration;
            }

            // Get fileinfo
            string resolvedPath = await connection.ResolvePathAsync(name, DuetAPI.Commands.FileDirectory.GCodes);
            if (!File.Exists(resolvedPath))
            {
                EndpointHelper.LogWarning(logger, $"Could not find file {name} (resolved to {resolvedPath})");
                return Results.Text("{\"err\":1}", "application/json");
            }
            GCodeFileInfo info = await connection.GetFileInfoAsync(resolvedPath, true);
            lock (_lastFileInfoLock)
            {
                _lastFileInfo = info;
                _lastFileInfo.FileName = resolvedPath;
            }

            // Return it in RRF format
            JsonObject result = new()
            {
                ["err"] = 0,
                ["fileName"] = name,
                ["size"] = info.Size
            };
            if (info.LastModified is not null)
            {
                result["lastModified"] = info.LastModified.Value.ToString("s");
            }
            result["height"] = Math.Round(info.Height, 2);
            result["layerHeight"] = Math.Round(info.LayerHeight, 2);
            result["numLayers"] = info.NumLayers;
            if (info.PrintTime is not null)
            {
                result["printTime"] = info.PrintTime.Value;
            }
            if (info.SimulatedTime is not null)
            {
                result["simulatedTime"] = info.SimulatedTime.Value;
            }
            if (info.Filament.Count > 0)
            {
                JsonArray filament = [];
                foreach (float value in info.Filament)
                {
                    filament.Add((JsonNode)Math.Round(value, 1));
                }
                result["filament"] = filament;
            }
            if (printDuration is not null)
            {
                result["printDuration"] = printDuration.Value;
            }
            if (info.Thumbnails.Count > 0)
            {
                JsonArray thumbnails = [];
                foreach (ThumbnailInfo thumbnail in info.Thumbnails)
                {
                    thumbnails.Add((JsonNode)new JsonObject
                    {
                        ["width"] = thumbnail.Width,
                        ["height"] = thumbnail.Height,
                        ["format"] = thumbnail.Format switch
                        {
                            ThumbnailInfoFormat.PNG => "png",
                            ThumbnailInfoFormat.JPEG => "jpeg",
                            ThumbnailInfoFormat.QOI => "qoi",
                            _ => "unknown"
                        },
                        ["offset"] = thumbnail.Offset,
                        ["size"] = thumbnail.Size
                    });
                }
                result["thumbnails"] = thumbnails;
            }
            result["generatedBy"] = info.GeneratedBy;
            return Results.Text(result.ToJsonString(), "application/json");
        }
        catch (Exception e)
        {
            EndpointHelper.LogError(logger, e, "Failed to handle rr_fileinfo request");
        }
        return Results.Text("{\"err\":1}", "application/json");
    }

    /// <summary>
    /// GET /rr_thumbnail?name={filename}&amp;offset={offset}
    /// Get the thumbnail from a given filename
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="name">G-code file to read thumbnails from</param>
    /// <param name="offset">Start offset of the thumbnail query</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> GetThumbnail(ILogger<RepRapFirmwareEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, string? name, long offset)
    {
        Settings settings = settingsMonitor.CurrentValue;
        try
        {
            // Filename defaults to the file being printed if it is not present
            using CommandConnection connection = await EndpointHelper.BuildConnectionAsync(settings.SocketPath);
            if (string.IsNullOrEmpty(name))
            {
                EndpointHelper.LogWarning(logger, "Missing name parameter in rr_thumbnail");
                return Results.Text("{\"err\":1}", "application/json");
            }

            // Get actual filename
            string resolvedPath = await connection.ResolvePathAsync(name);
            if (!File.Exists(resolvedPath))
            {
                EndpointHelper.LogWarning(logger, $"Could not find file {name} (resolved to {resolvedPath})");
                return Results.Text("{\"err\":1}", "application/json");
            }

            // Get fileinfo and cache it
            GCodeFileInfo? info = null;
            lock (_lastFileInfoLock)
            {
                if (_lastFileInfo is not null && _lastFileInfo.FileName == resolvedPath)
                {
                    info = _lastFileInfo;
                }
            }
            if (info is null)
            {
                info = await connection.GetFileInfoAsync(resolvedPath, true);
                lock (_lastFileInfoLock)
                {
                    _lastFileInfo = info;
                    _lastFileInfo.FileName = resolvedPath;
                }
            }

            // Get corresponding thumbnail
            string? data = null;
            foreach (ThumbnailInfo item in info.Thumbnails)
            {
                if (offset >= item.Offset && offset < item.Offset + item.Size)
                {
                    // NB: This only works because base64 data consists only of ASCII characters
                    data = item.Data?[(int)(offset - item.Offset)..];
                    break;
                }
            }

            // Return result
            if (data is null)
            {
                EndpointHelper.LogWarning(logger, "Failed to find corresponding thumbnail in rr_thumbnail");
                return Results.Text("{\"err\":1}", "application/json");
            }
            return Results.Text(JsonSerializer.Serialize(new ThumbnailResponse
            {
                FileName = name,
                Offset = offset,
                Data = data,
                Next = 0,
                Err = 0
            }, DwsJsonContext.Default.ThumbnailResponse), "application/json");
        }
        catch (Exception e)
        {
            EndpointHelper.LogError(logger, e, "Failed to handle rr_thumbnail request");
        }
        return Results.Text("{\"err\":1}", "application/json");
    }
}
