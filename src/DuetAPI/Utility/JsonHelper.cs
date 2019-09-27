using System;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DuetAPI.Utility
{
    /// <summary>
    /// Helper class for JSON serialization, deserialization, patch creation and patch application
    /// </summary>
    public static class JsonHelper
    {
        /// <summary>
        /// Default JSON (de-)serialization options
        /// </summary>
        public static readonly JsonSerializerOptions DefaultJsonOptions = new JsonSerializerOptions
        {
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Receive a serialized JSON object from a socket in UTF-8 format
        /// </summary>
        /// <param name="socket">Socket to read from</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Plain JSON</returns>
        public static async Task<MemoryStream> ReceiveUtf8Json(Socket socket, CancellationToken cancellationToken = default)
        {
            //Console.Write("IN ");

            MemoryStream json = new MemoryStream();
            bool inJson = false, inQuotes = false, isEscaped = false;
            int numBraces = 0;

            byte[] readData = new byte[1];
            while ((!inJson || numBraces > 0) && await socket.ReceiveAsync(readData, SocketFlags.None, cancellationToken) > 0)
            {
                char c = (char)readData[0];
                //Console.Write(c);
                if (inQuotes)
                {
                    if (isEscaped)
                    {
                        isEscaped = false;
                    }
                    else if (c == '\\')
                    {
                        isEscaped = true;
                    }
                    else if (c == '"')
                    {
                        inQuotes = false;
                    }
                }
                else if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == '{')
                {
                    inJson = true;
                    numBraces++;
                }
                else if (c == '}')
                {
                    numBraces--;
                }

                if (inJson)
                {
                    json.WriteByte(readData[0]);
                }
            }

            //Console.WriteLine(" OK");
            json.Seek(0, SeekOrigin.Begin);
            return json;
        }
    }
}
