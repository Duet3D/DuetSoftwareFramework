using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace DuetWebServer.Middleware
{
    /// <summary>
    /// Middleware class to redirect GET requests without dot in the path to the main index file
    /// </summary>
    public class FallbackMiddleware
    {
        /// <summary>
        /// Next request delegate in the pipeline
        /// </summary>
        private readonly RequestDelegate _next;

        /// <summary>
        /// Logger instance
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// Constructor of this middleware
        /// </summary>
        /// <param name="next">Next request delegate</param>
        /// <param name="logger">Logger instance</param>
        public FallbackMiddleware(RequestDelegate next, ILogger<FallbackMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        /// <summary>
        /// Method that is invoked when a new request is coming in.
        /// Redirects pages that could not be found to the index page
        /// </summary>
        /// <param name="context">HTTP context</param>
        /// <returns>Asynchronous task</returns>
        public async Task InvokeAsync(HttpContext context)
        {
            if (context.Request.Method == HttpMethods.Get &&
                !context.Request.Path.Value.StartsWith("/rr_") && !context.Request.Path.Value.StartsWith("/machine/") &&
                !context.Request.Path.Value.Contains("."))
            {
                _logger.LogWarning("Could not find resource {0}. Redirecting to /", context.Request.Path);
                context.Response.Redirect("/");
            }
            else
            {
                await _next(context);
            }
        }
    }
}
