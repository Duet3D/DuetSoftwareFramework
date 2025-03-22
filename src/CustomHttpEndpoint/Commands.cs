using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetAPIClient;

namespace CustomHttpEndpoint;

/// <summary>
/// Command handlers for the CustomHttpEndpoint
/// </summary>
public static class Commands
{
    /// <summary>
    /// Main command handler
    /// </summary>
    /// <param name="socketPath">UNIX socket path for IPC</param>
    /// <param name="quiet">Run command quietly</param>
    /// <param name="method">HTTP method for the endpoint</param>
    /// <param name="ns">Namespace</param>
    /// <param name="path">HTTP request path</param>
    /// <param name="cmd">Command to execute on HTTP request</param>
    /// <param name="cmdArgs">Arguments for the command</param>
    /// <returns>Exit code</returns>
    [UnsupportedOSPlatform("windows")]
    public static async Task<int> MainAsync(FileInfo socketPath, bool quiet, HttpEndpointType method, string ns, string path, string? cmd, string? cmdArgs)
    {
        if (method == HttpEndpointType.WebSocket && (!string.IsNullOrWhiteSpace(cmd) || !string.IsNullOrWhiteSpace(cmdArgs)))
        {
            Console.Error.WriteLine("Cannot use --exec parameter if method equals WebSocket");
            return 1;
        }

        // Create a new Command connection
        CommandConnection connection = new();
        try
        {
            await connection.ConnectAsync(socketPath.FullName);
        }
        catch (SocketException)
        {
            if (!quiet)
            {
                Console.Error.WriteLine("Failed to connect to DCS");
            }
            return 1;
        }

        // Create a new HTTP GET endpoint and keep listening for new requests
        try
        {
            bool websocketConnected = false;
            using HttpEndpointUnixSocket socket = await connection.AddHttpEndpointAsync(method, ns, path);
            socket.OnEndpointRequestReceived += async (unixSocket, requestConnection) =>
            {
                // Note that a call to ReadRequest can throw an exception in case DCS only created a test connection!
                // DCS may do that when an application attempts to register an existing endpoint twice

                if (method == HttpEndpointType.WebSocket)
                {
                    if (websocketConnected)
                    {
                        await requestConnection.SendResponseAsync(1000, "Demo application only supports one WebSocket connection");
                        return;
                    }

                    websocketConnected = true;
                    if (!quiet)
                    {
                        Console.WriteLine("WebSocket connected, type 'close' to close this connection");
                    }

                    try
                    {
                        using CancellationTokenSource cts = new();
                        Task webSocketTask = ReadFromWebSocketAsync(requestConnection, cts.Token);
                        Task consoleTask = ReadFromConsoleAsync(requestConnection, cts.Token);

                        await Task.WhenAny(webSocketTask, consoleTask);
                        cts.Cancel();
                    }
                    catch (Exception e)
                    {
                        if (e is not OperationCanceledException && e is not SocketException)
                        {
                            Console.WriteLine("Unexpected error:");
                            Console.WriteLine(e);
                        }
                    }
                    finally
                    {
                        websocketConnected = false;
                        if (!quiet)
                        {
                            Console.WriteLine("WebSocket disconnected");
                        }
                    }
                }
                else
                {
                    // Read the HTTP response from the client
                    ReceivedHttpRequest request = await requestConnection.ReadRequestAsync();

                    if (string.IsNullOrWhiteSpace(cmd))
                    {
                        // Write this event to the console if possible
                        if (!quiet)
                        {
                            Console.WriteLine("Got new HTTP request from session {0}", request.SessionId);
                        }

                        // Only print a demo response in case no process is supposed to be started
                        string response = $"This demo text has been returned from a third-party application.\n\nMethod: {method}\nSession ID: {request.SessionId}";
                        if (request.Headers.Count > 0)
                        {
                            response += "\n\nHeaders:";
                            foreach (var kv in request.Headers)
                            {
                                response += $"\n{kv.Key} = {kv.Value}";
                            }
                        }
                        if (request.Queries.Count > 0)
                        {
                            response += "\n\nQueries:";
                            foreach (var kv in request.Queries)
                            {
                                response += $"\n{kv.Key} = {kv.Value}";
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(request.Body))
                        {
                            response += "\n\nBody:\n" + request.Body;
                        }
                        await requestConnection.SendResponseAsync(200, response, HttpResponseType.PlainText);
                    }
                    else
                    {
                        // Replace query values in the arguments
                        string args = cmd;
                        foreach (var kv in request.Queries)
                        {
                            args = args.Replace($"%{kv.Key}%", kv.Value);
                        }

                        // Prepare the process start info
                        using Process process = new()
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = cmd,
                                Arguments = args,
                                RedirectStandardOutput = true
                            }
                        };

                        // Start a process and wait for it to exit
                        string output = "";
                        process.OutputDataReceived += (object sender, DataReceivedEventArgs e) => output += e.Data;
                        process.ErrorDataReceived += (object sender, DataReceivedEventArgs e) => output += e.Data;
                        if (process.Start())
                        {
                            process.BeginOutputReadLine();
                            process.BeginErrorReadLine();
                            process.WaitForExit();
                            await requestConnection.SendResponseAsync(200, output, HttpResponseType.PlainText);
                        }
                        else
                        {
                            await requestConnection.SendResponseAsync(501, "Failed to start process", HttpResponseType.StatusCode);
                        }
                    }
                }
            };

            // Display a message
            if (!quiet)
            {
                Console.WriteLine("{0} endpoint has been created and is now accessible via /machine/{1}/{2}", method, ns, path);
                if (method == HttpEndpointType.WebSocket)
                {
                    Console.WriteLine("IO from the first WebSocket connection will be redirected to stdio. Additional connections will be automatically closed.");
                }
                else if (string.IsNullOrWhiteSpace(cmd))
                {
                    Console.WriteLine("Press RETURN to close this program again");
                }
            }

            // Wait forever (or for Ctrl+C) in WebSocket mode or for the user to press RETURN in interactive REST mode.
            // If the connection is terminated while waiting, continue as well
            if (method == HttpEndpointType.WebSocket || string.IsNullOrWhiteSpace(cmd))
            {
                Task primaryTask = (method == HttpEndpointType.WebSocket) ? Task.Delay(-1) : Task.Run(() => Console.ReadLine());
                await Task.WhenAny(primaryTask, PollConnectionAsync(connection));
            }
        }
        catch (SocketException)
        {
            // You may want to try to unregister your endpoint here and try again...
            Console.WriteLine("Failed to create new HTTP socket. Perhaps another instance of this program is already running?");
        }
        finally
        {
            if (connection.IsConnected)
            {
                // Remove the endpoint again when the plugin is being unloaded
                await connection.RemoveHttpEndpointAsync(method, ns, path);
            }
        }
        return 0;
    }

    private static async Task PollConnectionAsync(BaseConnection connection)
    {
        try
        {
            do
            {
                await Task.Delay(2000);
                connection.Poll();
            }
            while (true);
        }
        catch (SocketException)
        {
            Console.WriteLine("Server has closed the connection");
            throw new OperationCanceledException();
        }
    }

    private static async Task ReadFromWebSocketAsync(HttpEndpointConnection connection, CancellationToken cancellationToken)
    {
        // Note that no content has been received when we get here for the first time.
        // In this case, it may take a while before/if data can be received from the client
        do
        {
            ReceivedHttpRequest websocketRequest = await connection.ReadRequestAsync(cancellationToken);
            Console.WriteLine(websocketRequest.Body);
        }
        while (!cancellationToken.IsCancellationRequested);
    }

    private static async Task ReadFromConsoleAsync(HttpEndpointConnection connection, CancellationToken cancellationToken)
    {
        do
        {
            string? input = await Task.Run(() => Console.ReadLine(), cancellationToken);
            if (input == "close")
            {
                // Sending codes greater than or equal to 1000 closes the connection
                await connection.SendResponseAsync(1000, "Connection closed", HttpResponseType.StatusCode, cancellationToken);
            }
            else
            {
                // Send input to the client
                await connection.SendResponseAsync(200, input ?? string.Empty, HttpResponseType.PlainText, cancellationToken);
            }
        }
        while (!cancellationToken.IsCancellationRequested);
    }
}