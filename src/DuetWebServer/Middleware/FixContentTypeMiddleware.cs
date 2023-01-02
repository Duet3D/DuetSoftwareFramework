using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;

namespace DuetWebServer.Middleware
{
    /// <summary>
    /// Middleware to fix incoming content types for code and upload request (PUT /machine/file/..., POST /machine/code, POST /rr_upload).
    /// Without this middleware, incorrect request content types cause the validation to fail and there are no discrete MVC attributes to fix this
    /// </summary>
    public class FixContentTypeMiddleware
    {
        /// <summary>
        /// Next request delegate to call
        /// </summary>
        private readonly RequestDelegate _next;

        /// <summary>
        /// Constructor of this middleware
        /// </summary>
        /// <param name="next">Next request delegate</param>
        public FixContentTypeMiddleware(RequestDelegate next) => _next = next;

        /// <summary>
        /// /rr_upload request
        /// </summary>
        private static PathString RrUploadRequest = new("/rr_upload");

        /// <summary>
        /// /machine/code request
        /// </summary>
        private static PathString MachineCodeRequest = new("/machine/code");

        /// <summary>
        /// /machine/file request
        /// </summary>
        private static PathString MachineFileRequest = new("/machine/file");

        /// <summary>
        /// Called when a new HTTP request is received
        /// </summary>
        /// <param name="context">HTTP context</param>
        /// <returns>Asynchronous task</returns>
        public async Task InvokeAsync(HttpContext context)
        {
            // Override incoming content types for certain requests
            if (context.Request.Method == "POST" && context.Request.Path.StartsWithSegments(RrUploadRequest))
            {
                context.Request.ContentType = "application/octet-stream";
            }
            else if (context.Request.Method == "POST" && context.Request.Path.StartsWithSegments(MachineCodeRequest))
            {
                context.Request.ContentType = "text/plain";
            }
            else if (context.Request.Method == "POST" && context.Request.Path.StartsWithSegments(MachineFileRequest))
            {
                context.Request.ContentType = "application/octet-stream";
            }

            // Call the next delegate/middleware in the pipeline
            await _next(context);
        }
    }
}
