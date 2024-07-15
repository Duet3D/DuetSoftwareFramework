using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace DuetWebServer.Middleware
{
    /// <summary>
    /// Middleware class to redirect GET requests without dot in the path to the main index file
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
            if (context.Request.Method == HttpMethods.Get &&
                !context.Request.Path.Value!.Equals("/") &&
                !context.Request.Path.Value.StartsWith("/rr_") && !context.Request.Path.Value.StartsWith("/machine/") &&
                !context.Request.Path.Value.Contains('.'))
            {
                logger.LogWarning("Could not find resource {Path}, serving index file", context.Request.Path);
                context.Request.Path = PathString.FromUriComponent("/");
            }
            await next(context);
        }
    }
}
