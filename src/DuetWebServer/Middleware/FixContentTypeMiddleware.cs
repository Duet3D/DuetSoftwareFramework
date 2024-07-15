using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;

namespace DuetWebServer.Middleware
{
    /// <summary>
    /// Middleware to fix incoming content types for code and upload request (PUT /machine/file/..., POST /machine/code, POST /rr_upload).
    /// Without this middleware, incorrect request content types cause the validation to fail and there are no discrete MVC attributes to fix this
    /// </summary>
    /// <param name="next">Next request delegate</param>
    public class FixContentTypeMiddleware(RequestDelegate next)
    {
        /// <summary>
        /// /rr_upload request
        /// </summary>
        private static readonly PathString RrUploadRequest = new("/rr_upload");

        /// <summary>
        /// /machine/code request
        /// </summary>
        private static readonly PathString MachineCodeRequest = new("/machine/code");

        /// <summary>
        /// /machine/file request
        /// </summary>
        private static readonly PathString MachineFileRequest = new("/machine/file");

        /// <summary>
        /// /machine/file/move request
        /// </summary>
        private static readonly PathString MachineFileMoveRequest = new("/machine/file/move");

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
            else if (context.Request.Method == "POST" && context.Request.Path.StartsWithSegments(MachineFileRequest) && !context.Request.Path.StartsWithSegments(MachineFileMoveRequest))
            {
                context.Request.ContentType = "application/octet-stream";
            }

            // Call the next delegate/middleware in the pipeline
            await next(context);
        }
    }
}
