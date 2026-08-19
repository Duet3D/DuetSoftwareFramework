using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace DuetWebServer.Middleware;

/// <summary>
/// Middleware class to redirect GET requests for client-side routes to the main index file
/// </summary>
/// <param name="logger">Logger instance</param>
public sealed class FallbackMiddleware(ILogger<FallbackMiddleware> logger) : IMiddleware
{
    /// <summary>
    /// Method that is invoked when a new request is coming in.
    /// Redirects pages that could not be found to the index page
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="next">Next request delegate</param>
    /// <returns>Asynchronous task</returns>
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // A request matched by a mapped endpoint is dispatched by the terminal middleware
        if (context.GetEndpoint() is not null)
        {
            await next(context);
            return;
        }

        string path = context.Request.Path.Value!;
        if (context.Request.Method == HttpMethods.Get &&
            !path.Equals("/") &&
            !path.StartsWith("/rr_") && !path.StartsWith("/machine/") &&
            // A path without a dot is a client-side route; Explorer and Jobs routes carry arbitrary
            // SD card paths (e.g. /Explorer/edit/sys/config.g or /Jobs/0.4mm%20Nozzle), so admit them explicitly
            (!path.Contains('.') || path.StartsWith("/Explorer/") || path.StartsWith("/Jobs/")))
        {
            logger.LogWarning("Could not find resource {Path}, serving index file", context.Request.Path);
            context.Request.Path = PathString.FromUriComponent("/");
        }
        await next(context);
    }
}
