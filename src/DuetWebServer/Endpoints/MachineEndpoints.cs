using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetAPIClient;
using DuetWebServer.Singletons;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DuetWebServer.Endpoints;

/// <summary>
/// Minimal-API endpoints for /machine requests
/// </summary>
public class MachineEndpoints
{
    /// <summary>
    /// Register the /machine endpoints
    /// </summary>
    /// <param name="app">Web application</param>
    public static void Map(WebApplication app)
    {
        RouteGroupBuilder machine = app.MapGroup("/machine").RequireAuthorization(Authorization.Policies.ReadOnly);

        machine.MapGet("/connect", Connect).AllowAnonymous();
        machine.MapGet("/noop", Noop);
        machine.MapGet("/disconnect", Disconnect).AllowAnonymous();
        machine.MapGet("/model", Model);
        machine.MapGet("/status", Model);
        machine.MapPost("/code", DoCode).RequireAuthorization(Authorization.Policies.ReadWrite);
        machine.MapGet("/file/{*filename}", DownloadFile);
        machine.MapPut("/file/{*filename}", UploadFile).RequireAuthorization(Authorization.Policies.ReadWrite);
        machine.MapGet("/fileinfo/{*filename}", GetFileInfo);
        machine.MapDelete("/file/{*filename}", DeleteFileOrDirectory).RequireAuthorization(Authorization.Policies.ReadWrite);
        machine.MapPost("/file/move", MoveFileOrDirectory).RequireAuthorization(Authorization.Policies.ReadWrite);
        machine.MapGet("/directory/{*directory}", GetFileList);
        machine.MapPut("/directory/{*directory}", CreateDirectory).RequireAuthorization(Authorization.Policies.ReadWrite);
        machine.MapPut("/plugin", InstallPlugin).RequireAuthorization(Authorization.Policies.ReadWrite);
        machine.MapDelete("/plugin", UninstallPlugin).RequireAuthorization(Authorization.Policies.ReadWrite);
        machine.MapPatch("/plugin", SetPluginData).RequireAuthorization(Authorization.Policies.ReadWrite);
        machine.MapPost("/startPlugin", StartPlugin).RequireAuthorization(Authorization.Policies.ReadWrite);
        machine.MapPost("/stopPlugin", StopPlugin).RequireAuthorization(Authorization.Policies.ReadWrite);
        machine.MapPut("/systemPackage", InstallSystemPackage).RequireAuthorization(Authorization.Policies.ReadWrite);
        machine.MapDelete("/systemPackage", UninstallSystemPackage).RequireAuthorization(Authorization.Policies.ReadWrite);
    }

    #region Authorization
    /// <summary>
    /// GET /machine/connect
    /// Check the password and register a new session on success
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="sessionStorage">Session storage singleton</param>
    /// <param name="password">Password to check</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> Connect(HttpContext context, ILogger<MachineEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, ISessionStorage sessionStorage, string? password)
    {
        Settings settings = settingsMonitor.CurrentValue;
        try
        {
            using CommandConnection connection = await EndpointHelper.BuildConnectionAsync(settings.SocketPath);
            if ((settings.OverrideWebPassword is null && await connection.CheckPasswordAsync(password ?? string.Empty)) ||
                (settings.OverrideWebPassword is not null && settings.OverrideWebPassword == (password ?? string.Empty)))
            {
                int sessionId = await connection.AddUserSessionAsync(AccessLevel.ReadWrite, SessionType.HTTP, context.Connection.RemoteIpAddress!.ToString());
                string sessionKey = sessionStorage.MakeSessionKey(sessionId, string.Empty, true);

                string jsonResponse = JsonSerializer.Serialize(new SessionKeyResponse { SessionKey = sessionKey }, DwsJsonContext.Default.SessionKeyResponse);
                return Results.Text(jsonResponse, "application/json");
            }
            return Results.Forbid();
        }
        catch (Exception e)
        {
            return await EndpointHelper.HandleDcsExceptionAsync(e, logger, settings, "Failed to handle connect request");
        }
    }

    /// <summary>
    /// GET /machine/noop
    /// Do nothing. May be used to ping the machine or to keep the HTTP session alive
    /// </summary>
    /// <returns>HTTP result</returns>
    private static IResult Noop() => Results.NoContent();

    /// <summary>
    /// GET /machine/disconnect
    /// Remove the current HTTP session again
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="sessionStorage">Session storage singleton</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> Disconnect(HttpContext context, ILogger<MachineEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, ISessionStorage sessionStorage)
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
            return await EndpointHelper.HandleDcsExceptionAsync(e, logger, settings, "Failed to handle disconnect request");
        }
    }
    #endregion

    #region General requests
    /// <summary>
    /// GET /machine/model and GET /machine/status
    /// Retrieve the full object model as JSON
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> Model(ILogger<MachineEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor)
    {
        Settings settings = settingsMonitor.CurrentValue;
        try
        {
            using CommandConnection connection = await EndpointHelper.BuildConnectionAsync(settings.SocketPath);
            string machineModel = await connection.GetSerializedObjectModelAsync();
            return Results.Text(machineModel, "application/json");
        }
        catch (Exception e)
        {
            return await EndpointHelper.HandleDcsExceptionAsync(e, logger, settings, "Failed to retrieve object model");
        }
    }

    /// <summary>
    /// POST /machine/code
    /// Execute plain G/M/T-code(s) from the request body and return the G-code response when done
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="sessionStorage">Session storage singleton</param>
    /// <param name="async">Execute code asynchronously (don't wait for a code result)</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> DoCode(HttpContext context, ILogger<MachineEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, ISessionStorage sessionStorage, bool async = false)
    {
        Settings settings = settingsMonitor.CurrentValue;

        string code;
        {
            using StreamReader reader = new(context.Request.Body, Encoding.UTF8);
            code = await reader.ReadToEndAsync();
        }

        try
        {
            if (!async)
            {
                sessionStorage.SetLongRunningHttpRequest(context.User, true);
            }

            try
            {
                using CommandConnection connection = await EndpointHelper.BuildConnectionAsync(settings.SocketPath);
                EndpointHelper.LogInformation(logger, $"Executing code '{code}'");
                return Results.Text(await connection.PerformSimpleCodeAsync(code, CodeChannel.HTTP, async));
            }
            finally
            {
                if (!async)
                {
                    sessionStorage.SetLongRunningHttpRequest(context.User, false);
                }
            }
        }
        catch (Exception e)
        {
            return await EndpointHelper.HandleDcsExceptionAsync(e, logger, settings, "Failed to perform code");
        }
    }
    #endregion

    #region File requests
    /// <summary>
    /// GET /machine/file/{filename}
    /// Download the specified file
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="filename">File to download</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> DownloadFile(ILogger<MachineEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, string filename)
    {
        Settings settings = settingsMonitor.CurrentValue;
        filename = HttpUtility.UrlDecode(filename);

        string resolvedPath = "n/a";
        try
        {
            resolvedPath = await EndpointHelper.ResolvePathAsync(settings.SocketPath, filename);
            if (!File.Exists(resolvedPath))
            {
                EndpointHelper.LogWarning(logger, $"Could not find file {filename} (resolved to {resolvedPath})");
                return Results.Text(HttpUtility.UrlPathEncode(filename), statusCode: StatusCodes.Status404NotFound);
            }

            FileStream stream = new(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Results.Stream(stream, "application/octet-stream");
        }
        catch (Exception e)
        {
            return await EndpointHelper.HandleDcsExceptionAsync(e, logger, settings, $"Failed download file {filename} (resolved to {resolvedPath})");
        }
    }

    /// <summary>
    /// PUT /machine/file/{filename}?timeModified={timeModified}
    /// Upload a file from the HTTP body and create the subdirectories if necessary
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="sessionStorage">Session storage singleton</param>
    /// <param name="filename">Destination of the file to upload</param>
    /// <param name="timeModified">Optional time indicating when the file was last modified</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> UploadFile(HttpContext context, ILogger<MachineEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, ISessionStorage sessionStorage, string filename, DateTime? timeModified)
    {
        Settings settings = settingsMonitor.CurrentValue;
        EndpointHelper.DisableRequestSizeLimit(context);
        filename = HttpUtility.UrlDecode(filename);

        string resolvedPath = "n/a";
        try
        {
            sessionStorage.SetLongRunningHttpRequest(context.User, true);
            try
            {
                resolvedPath = await EndpointHelper.ResolvePathAsync(settings.SocketPath, filename);

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
                        await context.Request.Body.CopyToAsync(stream);
                    }

                    // Move it into place
                    File.Move(partFile, resolvedPath, true);

                    // Change the datetime of the file if possible
                    if (timeModified is not null)
                    {
                        File.SetLastWriteTime(resolvedPath, timeModified.Value);
                    }
                }
                catch
                {
                    // Delete the file on error
                    File.Delete(partFile);
                    throw;
                }

                return TypedResults.Created(HttpUtility.UrlPathEncode(filename));
            }
            finally
            {
                sessionStorage.SetLongRunningHttpRequest(context.User, false);
            }
        }
        catch (Exception e)
        {
            return await EndpointHelper.HandleDcsExceptionAsync(e, logger, settings, $"Failed upload file {filename} (resolved to {resolvedPath})");
        }
    }

    /// <summary>
    /// GET /machine/fileinfo/{filename}?readThumbnailContent=true/false
    /// Parse a given G-code file and return information about this job file as a JSON object
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="filename">G-code file to analyze</param>
    /// <param name="readThumbnailContent">Whether thumbnail content may be read</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> GetFileInfo(ILogger<MachineEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, string filename, bool readThumbnailContent = false)
    {
        Settings settings = settingsMonitor.CurrentValue;
        filename = HttpUtility.UrlDecode(filename);

        string resolvedPath = "n/a";
        try
        {
            resolvedPath = await EndpointHelper.ResolvePathAsync(settings.SocketPath, filename);
            if (!File.Exists(resolvedPath))
            {
                EndpointHelper.LogWarning(logger, $"Could not find file {filename} (resolved to {resolvedPath})");
                return Results.Text(HttpUtility.UrlPathEncode(filename), statusCode: StatusCodes.Status404NotFound);
            }

            using CommandConnection connection = await EndpointHelper.BuildConnectionAsync(settings.SocketPath);
            GCodeFileInfo info = await connection.GetFileInfoAsync(resolvedPath, readThumbnailContent);

            string json = JsonSerializer.Serialize(info, ObjectModelContext.Default.GCodeFileInfo);
            return Results.Text(json, "application/json");
        }
        catch (Exception e)
        {
            return await EndpointHelper.HandleDcsExceptionAsync(e, logger, settings, $"Failed to retrieve file info for {filename} (resolved to {resolvedPath})");
        }
    }
    #endregion

    #region Shared File and Directory requests
    /// <summary>
    /// DELETE /machine/file/{filename}
    /// Delete the given file or directory
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="filename">File or directory to delete</param>
    /// <param name="recursive">Whether the directory shall be deleted recursively</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> DeleteFileOrDirectory(ILogger<MachineEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, string filename, bool recursive = false)
    {
        Settings settings = settingsMonitor.CurrentValue;
        filename = HttpUtility.UrlDecode(filename);

        string resolvedPath = "n/a";
        try
        {
            resolvedPath = await EndpointHelper.ResolvePathAsync(settings.SocketPath, filename);

            if (Directory.Exists(resolvedPath))
            {
                Directory.Delete(resolvedPath, recursive);
                return Results.NoContent();
            }

            if (File.Exists(resolvedPath))
            {
                File.Delete(resolvedPath);
                return Results.NoContent();
            }

            EndpointHelper.LogWarning(logger, $"Could not find file {filename} (resolved to {resolvedPath})");
            return Results.Text(HttpUtility.UrlPathEncode(filename), statusCode: StatusCodes.Status404NotFound);
        }
        catch (Exception e)
        {
            return await EndpointHelper.HandleDcsExceptionAsync(e, logger, settings, $"Failed to delete file {filename} (resolved to {resolvedPath})");
        }
    }

    /// <summary>
    /// POST /machine/file/move
    /// Move a file or directory from a to b
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> MoveFileOrDirectory(HttpContext context, ILogger<MachineEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor)
    {
        Settings settings = settingsMonitor.CurrentValue;

        IFormCollection form = await context.Request.ReadFormAsync();
        string from = form["from"].ToString();
        string to = form["to"].ToString();
        bool force = bool.TryParse(form["force"], out bool forceValue) && forceValue;

        string source = "n/a", destination = "n/a";
        try
        {
            source = await EndpointHelper.ResolvePathAsync(settings.SocketPath, from);
            destination = await EndpointHelper.ResolvePathAsync(settings.SocketPath, to);

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
                        return TypedResults.Conflict();
                    }
                }

                Directory.Move(source, destination);
                return Results.NoContent();
            }

            // Deal with files
            if (File.Exists(source))
            {
                if (File.Exists(destination))
                {
                    if (force)
                    {
                        File.Delete(destination);
                    }
                    else
                    {
                        return TypedResults.Conflict();
                    }
                }

                File.Move(source, destination);
                return Results.NoContent();
            }

            return force ? Results.NoContent() : Results.Text(HttpUtility.UrlPathEncode(from), statusCode: StatusCodes.Status404NotFound);
        }
        catch (Exception e)
        {
            return await EndpointHelper.HandleDcsExceptionAsync(e, logger, settings, $"Failed to move file {from} to {to} (resolved to {source} and {destination})");
        }
    }
    #endregion

    #region Directory requests
    /// <summary>
    /// GET /machine/directory/{directory}
    /// Get a file list of the specified directory
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="directory">Directory to query</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> GetFileList(ILogger<MachineEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, string? directory)
    {
        Settings settings = settingsMonitor.CurrentValue;
        directory = HttpUtility.UrlDecode(directory);

        string resolvedPath = "n/a";
        try
        {
            resolvedPath = await EndpointHelper.ResolvePathAsync(settings.SocketPath, directory ?? string.Empty);
            if (!Directory.Exists(resolvedPath))
            {
                EndpointHelper.LogWarning(logger, $"Could not find directory {directory} (resolved to {resolvedPath})");
                return Results.Text(HttpUtility.UrlPathEncode(directory), statusCode: StatusCodes.Status404NotFound);
            }
            return Results.Bytes(FileLists.GetFileListUtf8(directory ?? string.Empty, resolvedPath), "application/json");
        }
        catch (Exception e)
        {
            return await EndpointHelper.HandleDcsExceptionAsync(e, logger, settings, $"Failed to retrieve file list for {directory} (resolved to {resolvedPath})");
        }
    }

    /// <summary>
    /// PUT /machine/directory/{directory}
    /// Create the given directory
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="directory">Directory to create</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> CreateDirectory(ILogger<MachineEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, string directory)
    {
        Settings settings = settingsMonitor.CurrentValue;
        directory = HttpUtility.UrlDecode(directory);

        string resolvedPath = "n/a";
        try
        {
            resolvedPath = await EndpointHelper.ResolvePathAsync(settings.SocketPath, directory);
            Directory.CreateDirectory(resolvedPath);
            return TypedResults.Created(HttpUtility.UrlPathEncode(directory));
        }
        catch (Exception e)
        {
            return await EndpointHelper.HandleDcsExceptionAsync(e, logger, settings, $"Failed to create directory {directory} (resolved to {resolvedPath})");
        }
    }
    #endregion

    #region Plugins
    /// <summary>
    /// PUT /machine/plugin
    /// Install or upgrade a plugin ZIP file
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="sessionStorage">Session storage singleton</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> InstallPlugin(HttpContext context, ILogger<MachineEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, ISessionStorage sessionStorage)
    {
        Settings settings = settingsMonitor.CurrentValue;
        EndpointHelper.DisableRequestSizeLimit(context);

        string zipFile = Path.GetTempFileName();
        try
        {
            sessionStorage.SetLongRunningHttpRequest(context.User, true);
            try
            {
                // Write ZIP file
                await using (FileStream stream = new(zipFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await context.Request.Body.CopyToAsync(stream);
                }

                // Install it
                using CommandConnection connection = await EndpointHelper.BuildConnectionAsync(settings.SocketPath);
                await connection.InstallPluginAsync(zipFile);

                return Results.NoContent();
            }
            catch (Exception e)
            {
                return await EndpointHelper.HandleDcsExceptionAsync(e, logger, settings, $"Failed to upload ZIP file to {zipFile}");
            }
        }
        finally
        {
            sessionStorage.SetLongRunningHttpRequest(context.User, false);
            File.Delete(zipFile);
        }
    }

    /// <summary>
    /// DELETE /machine/plugin
    /// Uninstall a plugin
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="sessionStorage">Session storage singleton</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> UninstallPlugin(HttpContext context, ILogger<MachineEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, ISessionStorage sessionStorage)
    {
        Settings settings = settingsMonitor.CurrentValue;
        try
        {
            sessionStorage.SetLongRunningHttpRequest(context.User, true);
            try
            {
                // Get the plugin name
                string pluginName;
                using (StreamReader reader = new(context.Request.Body))
                {
                    pluginName = await reader.ReadToEndAsync();
                }

                // Uninstall it
                using CommandConnection connection = await EndpointHelper.BuildConnectionAsync(settings.SocketPath);
                await connection.UninstallPluginAsync(pluginName);

                return Results.NoContent();
            }
            finally
            {
                sessionStorage.SetLongRunningHttpRequest(context.User, false);
            }
        }
        catch (Exception e)
        {
            return await EndpointHelper.HandleDcsExceptionAsync(e, logger, settings, "Failed to uninstall plugin");
        }
    }

    /// <summary>
    /// PATCH /machine/plugin
    /// Set plugin data in the object model if there is no SBC executable
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> SetPluginData(HttpContext context, ILogger<MachineEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor)
    {
        Settings settings = settingsMonitor.CurrentValue;
        EndpointHelper.DisableRequestSizeLimit(context);
        try
        {
            PluginPatchInstruction instruction = (await JsonSerializer.DeserializeAsync(context.Request.Body, DwsJsonContext.Default.PluginPatchInstruction))!;

            using CommandConnection connection = await EndpointHelper.BuildConnectionAsync(settings.SocketPath);
            ObjectModel model = await connection.GetObjectModelAsync();
            if (model.Plugins.TryGetValue(instruction.Plugin, out Plugin plugin))
            {
                if (!string.IsNullOrEmpty(plugin.SbcExecutable))
                {
                    EndpointHelper.LogWarning(logger, $"Tried to set plugin data for {plugin.Id} but it has an SBC executable set");
                    return Results.Forbid();
                }

                await connection.SetPluginDataAsync(instruction.Key, instruction.Value, instruction.Plugin);
                return Results.NoContent();
            }
            return TypedResults.NotFound();
        }
        catch (Exception e)
        {
            return await EndpointHelper.HandleDcsExceptionAsync(e, logger, settings, "Failed to set plugin data");
        }
    }

    /// <summary>
    /// POST /machine/startPlugin
    /// Start a plugin on the SBC
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> StartPlugin(HttpContext context, ILogger<MachineEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor)
    {
        Settings settings = settingsMonitor.CurrentValue;
        try
        {
            // Get the plugin name
            string pluginName;
            using (StreamReader reader = new(context.Request.Body))
            {
                pluginName = await reader.ReadToEndAsync();
            }

            // Start it
            using CommandConnection connection = await EndpointHelper.BuildConnectionAsync(settings.SocketPath);
            await connection.StartPluginAsync(pluginName);

            return Results.NoContent();
        }
        catch (Exception e)
        {
            return await EndpointHelper.HandleDcsExceptionAsync(e, logger, settings, "Failed to start plugin");
        }
    }

    /// <summary>
    /// POST /machine/stopPlugin
    /// Stop a plugin on the SBC
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> StopPlugin(HttpContext context, ILogger<MachineEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor)
    {
        Settings settings = settingsMonitor.CurrentValue;
        try
        {
            // Get the plugin name
            string pluginName;
            using (StreamReader reader = new(context.Request.Body))
            {
                pluginName = await reader.ReadToEndAsync();
            }

            // Stop it
            using CommandConnection connection = await EndpointHelper.BuildConnectionAsync(settings.SocketPath);
            await connection.StopPluginAsync(pluginName);

            return Results.NoContent();
        }
        catch (Exception e)
        {
            return await EndpointHelper.HandleDcsExceptionAsync(e, logger, settings, "Failed to stop plugin");
        }
    }
    #endregion

    #region System packages
    /// <summary>
    /// PUT /machine/systemPackage
    /// Install or upgrade a system package
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="sessionStorage">Session storage singleton</param>
    /// <param name="applicationLifetime">Application lifecycle instance</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> InstallSystemPackage(HttpContext context, ILogger<MachineEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, ISessionStorage sessionStorage, IHostApplicationLifetime applicationLifetime)
    {
        Settings settings = settingsMonitor.CurrentValue;
        EndpointHelper.DisableRequestSizeLimit(context);

        string packageFile = Path.GetTempFileName();
        try
        {
            sessionStorage.SetLongRunningHttpRequest(context.User, true);
            try
            {
                // Write package file
                await using (FileStream stream = new(packageFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await context.Request.Body.CopyToAsync(stream);
                }

                // Install it
                try
                {
                    using CommandConnection connection = await EndpointHelper.BuildConnectionAsync(settings.SocketPath);
                    await connection.InstallSystemPackageAsync(packageFile, applicationLifetime.ApplicationStopping);
                }
                catch (OperationCanceledException)
                {
                    EndpointHelper.LogWarning(logger, "Application is shutting down due to system package update");
                }
                return Results.NoContent();
            }
            catch (Exception e)
            {
                return await EndpointHelper.HandleDcsExceptionAsync(e, logger, settings, $"Failed to upload package file to {packageFile}");
            }
        }
        finally
        {
            sessionStorage.SetLongRunningHttpRequest(context.User, false);
            File.Delete(packageFile);
        }
    }

    /// <summary>
    /// DELETE /machine/systemPackage
    /// Uninstall a system package
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="sessionStorage">Session storage singleton</param>
    /// <returns>HTTP result</returns>
    private static async Task<IResult> UninstallSystemPackage(HttpContext context, ILogger<MachineEndpoints> logger, IOptionsMonitor<Settings> settingsMonitor, ISessionStorage sessionStorage)
    {
        Settings settings = settingsMonitor.CurrentValue;
        try
        {
            sessionStorage.SetLongRunningHttpRequest(context.User, true);
            try
            {
                // Get the package name
                string package;
                using (StreamReader reader = new(context.Request.Body))
                {
                    package = await reader.ReadToEndAsync();
                }

                // Uninstall it
                using CommandConnection connection = await EndpointHelper.BuildConnectionAsync(settings.SocketPath);
                await connection.UninstallSystemPackageAsync(package);

                return Results.NoContent();
            }
            finally
            {
                sessionStorage.SetLongRunningHttpRequest(context.User, false);
            }
        }
        catch (Exception e)
        {
            return await EndpointHelper.HandleDcsExceptionAsync(e, logger, settings, "Failed to uninstall system package");
        }
    }
    #endregion
}
