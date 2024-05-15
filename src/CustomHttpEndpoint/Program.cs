using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// HTTP method to provide
/// </summary>
HttpEndpointType method = HttpEndpointType.GET;

/// <summary>
/// Command binary to start when an HTTP query is received
/// </summary>
string? cmd = null;

/// <summary>
/// Command arguments for the binary to start
/// </summary>
string? cmdArgs = null;

/// <summary>
/// True if no info texts are printed
/// </summary>
bool quiet = false;

// Parse the command line arguments
string? lastArg = null, socketPath = Defaults.FullSocketPath, ns = "custom-http-endpoint", path = "demo";
foreach (string arg in args)
{
    if (lastArg == "-s" || lastArg == "--socket")
    {
        socketPath = arg;
    }
    else if (lastArg == "-m" || lastArg == "--method")
    {
        if (!Enum.TryParse(arg, true, out method))
        {
            Console.WriteLine("Error: Invalid HTTP method");
            return 2;
        }
    }
    else if (lastArg == "-n" || lastArg == "--namespace")
    {
        ns = arg;
    }
    else if (lastArg == "-p" || lastArg == "--path")
    {
        path = arg;
    }
    else if (lastArg == "-e" || lastArg == "--exec")
    {
        cmd = arg;
    }
    else if (lastArg == "-a" || lastArg == "--args")
    {
        cmdArgs = arg;
    }
    else if (arg == "-q" || lastArg == "--quiet")
    {
        quiet = true;
    }
    else if (arg == "-h" || arg == "--help")
    {
        Console.WriteLine("Create a custom HTTP endpoint in the format /machine/{namespace}/{path}");
        Console.WriteLine("Available command line options:");
        Console.WriteLine("-s, --socket <socket>: UNIX socket to connect to");
        Console.WriteLine("-m, --method [GET, POST, PUT, PATCH, TRACE, DELETE, OPTIONS, WebSocket]: HTTP method to use (defaults to GET)");
        Console.WriteLine("-n, --namespace <namespace>: Namespace to use (defaults to custom-http-endpoint)");
        Console.WriteLine("-p, --path <path>: HTTP query path (defaults to demo)");
        Console.WriteLine("-e, --exec <executable>: Command to execute when an HTTP query is received, stdout and stderr are returned as the response body");
        Console.WriteLine("-a, --args <arguments>: Arguments for the executable command. Query values in % chars are replaced with query options (e.g. %myvalue%). Not applicable for WebSockets");
        Console.WriteLine("-q, --quiet: Do not display info text");
        Console.WriteLine("-h, --help: Displays this text");
        return 0;
    }
    lastArg = arg;
}
if (method == HttpEndpointType.WebSocket && (!string.IsNullOrWhiteSpace(cmd) || !string.IsNullOrWhiteSpace(cmdArgs)))
{
    Console.WriteLine("Error: Cannot use --exec parameter if method equals WebSocket");
}

// Create a new Command connection
CommandConnection connection = new();
try
{
    await connection.Connect(socketPath);
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
    using HttpEndpointUnixSocket socket = await connection.AddHttpEndpoint(method, ns, path);
    socket.OnEndpointRequestReceived += async (HttpEndpointUnixSocket unixSocket, HttpEndpointConnection requestConnection) =>
    {
        // Note that a call to ReadRequest can throw an exception in case DCS only created a test connection!
        // DCS may do that when an application attempts to register an existing endpoint twice

        if (method == HttpEndpointType.WebSocket)
        {
            if (websocketConnected)
            {
                await requestConnection.SendResponse(1000, "Demo application only supports one WebSocket connection");
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
                Task webSocketTask = ReadFromWebSocket(requestConnection, cts.Token);
                Task consoleTask = ReadFromConsole(requestConnection, cts.Token);

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
            ReceivedHttpRequest request = await requestConnection.ReadRequest();

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
                await requestConnection.SendResponse(200, response, HttpResponseType.PlainText);
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
                    await requestConnection.SendResponse(200, output, HttpResponseType.PlainText);
                }
                else
                {
                    await requestConnection.SendResponse(501, "Failed to start process", HttpResponseType.StatusCode);
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
        await Task.WhenAny(primaryTask, PollConnection(connection));
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
        await connection.RemoveHttpEndpoint(method, ns, path);
    }
}

return 0;

/// <summary>
/// Poll the connection every two seconds to notice if the server has closed the connection
/// </summary>
/// <param name="connection">Connection to poll</param>
/// <returns>Asynchronous task</returns>
static async Task PollConnection(BaseConnection connection)
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

/// <summary>
/// Read another message from the web socket and print it to the console
/// </summary>
/// <param name="connection">WebSocket connection</param>
/// <param name="cancellationToken">Cancellation token</param>
/// <returns>Asynchronous task</returns>
static async Task ReadFromWebSocket(HttpEndpointConnection connection, CancellationToken cancellationToken)
{
    // Note that no content has been received when we get here for the first time.
    // In this case, it may take a while before/if data can be received from the client
    do
    {
        ReceivedHttpRequest websocketRequest = await connection.ReadRequest(cancellationToken);
        Console.WriteLine(websocketRequest.Body);
    }
    while (!cancellationToken.IsCancellationRequested);
}

/// <summary>
/// Read a message from the console and send it to the client
/// </summary>
/// <param name="connection">WebSocket connection</param>
/// <param name="cancellationToken">Cancellation token</param>
/// <returns>Asynchronous task</returns>
static async Task ReadFromConsole(HttpEndpointConnection connection, CancellationToken cancellationToken)
{
    do
    {
        string? input = await Task.Run(() => Console.ReadLine(), cancellationToken);
        if (input == "close")
        {
            // Sending codes greater than or equal to 1000 closes the connection
            await connection.SendResponse(1000, "Connection closed", HttpResponseType.StatusCode, cancellationToken);
        }
        else
        {
            // Send input to the client
            await connection.SendResponse(200, input ?? string.Empty, HttpResponseType.PlainText, cancellationToken);
        }
    }
    while (!cancellationToken.IsCancellationRequested);
}

