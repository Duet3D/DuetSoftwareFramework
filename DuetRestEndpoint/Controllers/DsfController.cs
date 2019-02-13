using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace DuetRestEndpoint.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class DsfController : ControllerBase
    {
        // GET dsf/connect
        [HttpGet("connect")]
        public async Task<IActionResult> GetAsync()
        {
            System.Diagnostics.Debug.WriteLine("Debug message");
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
                if (webSocket.State == WebSocketState.Open)
                {
                    while (!HttpContext.RequestAborted.IsCancellationRequested)
                    {
                        var response = string.Format("Hello! Time {0}", DateTime.Now.ToString());
                        var bytes = System.Text.Encoding.UTF8.GetBytes(response);

                        await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

                        await Task.Delay(2000);
                    }
                }
            }
            return StatusCode(101);
        }

        // POST dsf/code
        [HttpPost]
        public ActionResult<string> DoCode([FromBody] string code)
        {
            return "code";
        }

        // GET dsf/files/<filename>
        [HttpGet("files/{filename}")]
        public ActionResult<string> DownloadFile(string filename)
        {
            return "value";
        }

        // POST dsf/files/<filename>
        [HttpPost("files/{filename}")]
        public ActionResult UploadFile(string filename, [FromBody] string content)
        {
            return StatusCode(201);
        }

        // DELETE dsf/files/<filename>
        [HttpDelete("{filename}")]
        public ActionResult DeleteFile(string filename)
        {
            return StatusCode(204);
        }

        // GET dsf/fileinfo/<filename>
        [HttpGet("fileinfo/{filename}")]
        public ActionResult<string> GetFileinfo(string filename)
        {
            return "value";
        }
    }
}
