using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace DuetWebServer.Middleware;

/// <summary>
/// Middleware class to redirect GET requests for client-side routes to the main index file
/// </summary>
/// <param name="next">Next request delegate</param>
/// <param name="logger">Logger instance</param>
public class FallbackMiddleware(RequestDelegate next, ILogger<FallbackMiddleware> logger)
{
    /// <summary>
    /// Method that is invoked when a new request is coming in.
    /// Redirects pages that could not be found to the index page
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <returns>Asynchronous task</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        string path = context.Request.Path.Value!;
        if (context.Request.Method == HttpMethods.Get &&
            !path.Equals("/") &&
            !path.StartsWith("/rr_") && !path.StartsWith("/machine/") &&
            // A path without a dot is a client-side route; the Explorer editor (e.g.
            // /Explorer/edit/sys/config.g) is one too but carries the target filename, so admit it explicitly
            (!path.Contains('.') || path.StartsWith("/Explorer/")))
        {
            logger.LogWarning("Could not find resource {Path}, serving index file", context.Request.Path);
            context.Request.Path = PathString.FromUriComponent("/");
        }
        await next(context);
    }
}
