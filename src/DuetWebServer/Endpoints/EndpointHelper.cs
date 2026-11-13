using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPIClient;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace DuetWebServer.Endpoints;

/// <summary>
/// Shared helpers for the minimal-API endpoint handlers
/// </summary>
internal static class EndpointHelper
{
    /// <summary>
    /// Lift the request body size limit for the current upload request
    /// </summary>
    /// <param name="context">HTTP context</param>
    public static void DisableRequestSizeLimit(HttpContext context)
    {
        IHttpMaxRequestBodySizeFeature? feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is not null && !feature.IsReadOnly)
        {
            feature.MaxRequestBodySize = null;
        }
    }

    /// <summary>
    /// Restore path separators that were sent percent-encoded
    /// </summary>
    /// <param name="path">Path taken from a catch-all route parameter</param>
    /// <returns>Path with its separators restored</returns>
    /// <remarks>
    /// ASP.NET decodes route parameters but leaves %2F alone so that it cannot be mistaken for a segment
    /// separator, whereas DWC sends virtual paths with encoded slashes. Only that one escape may be
    /// resolved here; decoding the whole value again would eat a literal percent sign in a file name and
    /// turn a plus into a space
    /// </remarks>
    [return: NotNullIfNotNull(nameof(path))]
    public static string? RestorePathSeparators(string? path) => path?.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Log an information
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="message">Message</param>
    /// <param name="memberName">Handler calling this method</param>
    public static void LogInformation(ILogger logger, string message, [CallerMemberName] string memberName = "") => logger.LogInformation("[{method}] {message}", memberName, message);

    /// <summary>
    /// Log a warning
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="message">Message</param>
    /// <param name="memberName">Handler calling this method</param>
    public static void LogWarning(ILogger logger, string message, [CallerMemberName] string memberName = "") => logger.LogWarning("[{method}] {message}", memberName, message);

    /// <summary>
    /// Log a warning
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="exception">Exception</param>
    /// <param name="message">Message</param>
    /// <param name="memberName">Handler calling this method</param>
    public static void LogWarning(ILogger logger, Exception? exception, string message, [CallerMemberName] string memberName = "") => logger.LogWarning(exception, "[{method}] {message}", memberName, message);

    /// <summary>
    /// Log an error
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="message">Message</param>
    /// <param name="memberName">Handler calling this method</param>
    public static void LogError(ILogger logger, string message, [CallerMemberName] string memberName = "") => logger.LogError("[{method}] {message}", memberName, message);

    /// <summary>
    /// Log an error
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="exception">Exception</param>
    /// <param name="message">Message</param>
    /// <param name="memberName">Handler calling this method</param>
    public static void LogError(ILogger logger, Exception? exception, string message, [CallerMemberName] string memberName = "") => logger.LogError(exception, "[{method}] {message}", memberName, message);

    /// <summary>
    /// Build a new command connection to DCS
    /// </summary>
    /// <param name="socketPath">Path to the DSF IPC socket</param>
    /// <returns>Command connection</returns>
    public static async Task<CommandConnection> BuildConnectionAsync(string socketPath)
    {
        CommandConnection connection = new();
        await connection.ConnectAsync(socketPath);
        return connection;
    }

    /// <summary>
    /// Resolve a virtual path to a physical one using DCS
    /// </summary>
    /// <param name="socketPath">Path to the DSF IPC socket</param>
    /// <param name="path">Path to resolve</param>
    /// <returns>Physical path</returns>
    public static async Task<string> ResolvePathAsync(string socketPath, string path)
    {
        using CommandConnection connection = await BuildConnectionAsync(socketPath);
        return await connection.ResolvePathAsync(path);
    }

    /// <summary>
    /// Check if a path refers to the root directory of a volume
    /// </summary>
    /// <param name="connection">Connection to resolve the volume roots with</param>
    /// <param name="path">RepRapFirmware-style path as passed by the client</param>
    /// <param name="resolvedPath">Physical path it was resolved to</param>
    /// <returns>Whether the resolved path is the base directory or the root directory of the requested volume</returns>
    public static async Task<bool> IsVolumeRootAsync(CommandConnection connection, string path, string resolvedPath)
    {
        resolvedPath = NormalizePath(resolvedPath);
        if (resolvedPath == NormalizePath(await connection.ResolvePathAsync("0:/")))
        {
            return true;
        }

        int colonIndex = path.IndexOf(':');
        return colonIndex > 0 && path.AsSpan(0, colonIndex).IndexOfAnyExceptInRange('0', '9') < 0 && resolvedPath == NormalizePath(await connection.ResolvePathAsync(path[..(colonIndex + 1)] + '/'));
    }

    /// <summary>
    /// Resolve relative segments of a physical path and strip the trailing directory separator
    /// </summary>
    /// <param name="physicalPath">Physical path to normalize</param>
    /// <returns>Normalized path</returns>
    private static string NormalizePath(string physicalPath) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(physicalPath));

    /// <summary>
    /// Map a DCS-related exception to the standard /machine error response
    /// </summary>
    /// <param name="e">Exception to handle</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settings">Application settings</param>
    /// <param name="fallbackMessage">Log message for the generic error case</param>
    /// <param name="memberName">Handler calling this method</param>
    /// <returns>Error result</returns>
    public static async Task<IResult> HandleDcsExceptionAsync(Exception e, ILogger logger, Settings settings, string fallbackMessage, [CallerMemberName] string memberName = "")
    {
        if (e is AggregateException ae)
        {
            e = ae.InnerException!;
        }
        if (e is IncompatibleVersionException)
        {
            logger.LogError("[{method}] {message}", memberName, "Incompatible DCS version");
            return Results.Text("Incompatible DCS version", statusCode: StatusCodes.Status502BadGateway);
        }
        if (e is SocketException)
        {
            if (File.Exists(settings.StartErrorFile))
            {
                string startError = await File.ReadAllTextAsync(settings.StartErrorFile);
                logger.LogError("[{method}] {message}", memberName, startError);
                return Results.Text(startError, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            logger.LogError("[{method}] {message}", memberName, "DCS is not started");
            return Results.Text("Failed to connect to Duet, please check your connection (DCS is not started)", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        logger.LogWarning(e, "[{method}] {message}", memberName, fallbackMessage);
        return Results.Text(e.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
}
