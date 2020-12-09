# Duet Software Framework

![Version](https://img.shields.io/github/v/release/chrishamm/DuetSoftwareFramework) ![License](https://img.shields.io/github/license/chrishamm/DuetSoftwareFramework?color=blue) ![Issues](https://img.shields.io/github/issues/chrishamm/DuetWebControl?color=blue)

Duet Software Framework resembles a collection of programs to control an attached Duet3D board from a Linux-based mini computer (SBC). Since it is using .NET Core, it requires an ARM processor that supports ARMv7 instructions processor is required (Raspberry Pi 2 or newer).

## Installation

There are multiple options for installing Duet Software Framework on your Linux board.

### 1. DuetPi Linux image

You may flash the official firmware image by Duet3D to your SD card. Follow the instructions on the [Duet 3 setup](https://duet3d.dozuki.com/Wiki/SBC_Setup_for_Duet_3) page for further details. This is applicable for Raspberry Pi-based boards except for the Raspberry 1 and Zero, which are powered by an ARMv6 processor.

### 2. Apt package feed

The official DuetPi image uses Raspbian under the hood. Since Raspbian itself is based on Debian, Duet3D provides package feeds for automated updates.
The following package feeds are available:

| Type | Architecture | URL | Debian sources.list.d file |
| ---- | ------------ | --- | ---------------------------|
| stable | armv7 | http://pkg.duet3d.com/dists/stable/armv7/binary-armhf/ | deb https://pkg.duet3d.com/ stable armv7 |
| unstable | armv7 | http://pkg.duet3d.com/dists/unstable/armv7/binary-armhf/ | deb https://pkg.duet3d.com/ unstable armv7 |
| stable | aarch64 | http://pkg.duet3d.com/dists/stable/armv7/binary-arm64/ | deb https://pkg.duet3d.com/ stable armv7 |
| unstable | aarch64 | http://pkg.duet3d.com/dists/unstable/armv7/binary-arm64/ | deb https://pkg.duet3d.com/ unstable armv7 |

The stable feed is suitable for production whereas Duet3D encourages users to give the unstable feed a try. It contains new and experimental features that need to be tested.

In order to use one of these feeds, create a new file `/etc/apt/sources.list.d/duet3d.list` and copy the file contents from one of the feeds above.
In additon, it is necessary to add a new GPG certificate for the Duet3D package feed:

```
wget -q https://pkg.duet3d.com/duet3d.gpg
sudo mv duet3d.gpg /etc/apt/trusted.gpg.d/
sudo chown root:root /etc/apt/trusted.gpg.d/duet3d.gpg
https://pkg.duet3d.com/duet3d.gpg
```

After that, update your apt package lists and install `duetsoftwareframework`:

```
sudo apt-get update
sudo apt-get install duetsoftwareframework
```

Since the software packages come with systemd services, you can enable both the `duetcontrolserver` and `duetwebserver` services if you wish to start them on boot:

```
sudo systemctl enable duetcontrolserver
sudo systemctl enable duetwebserver
```

If you replace `enable` with `start` in the lines above, you can start these services.

### 3. Make your own binaries

See [Building](#building) for futher information.

## DuetControlServer

This application is the heart component of Duet Software Framework. It takes takes care of  G/M/T-code processing, inter-process communication, file path mapping, and communication with RepRapFirmware as well as firmware updates.
If you want to write your own plugins, this is the component that you need to connect to.

### Command-Line Options

The following command-line arguments are available:

- `-u`, `--update`: Update RepRapFirmware and exit. This works even if another instance is already started
- `-l`, `--log-level`: Set the minimum log level. Valid options are: `trace`, `debug` , `info` , `warn`, `error`, `fatal`, `off` Default is `info`
- `-c`, `--config`: Override the path to the JSON configuration file. Defaults to `/opt/dsf/conf/config.json`
- `-r`, `--no-reset-stop`: Do not terminate this application when M999 has been processed
- `-S`, `--socket-directory`: Override the path where DCS creates UNIX sockets. Defaults to `/var/run/dsf`
- `-s`, `--socket-file`: Override the filename of DCS's UNIX socket. Defaults to `dcs.sock`
- `-b`, `--base-directory`: Set the base directory of the virtual SD card directoy. This is used for RepRapFirmware compatibility. Defaults to `/opt/dsf/sd`
- `-D`, `--no-spi`: Do NOT connect over SPI. Not recommended, use at your own risk!
- `-h`, `--help`: Display all available command-line parameters

Note that all the command-line options are case-sensitive.

### SPI Link

In order to connect to the firmware, a binary data protocol is used. DuetControlServer attaches to the Duet using an SPI connection (typically `/dev/spidev0.0`) in master mode. In addition, a GPIO pin (typically pin 22 on the Raspberry Pi header via `/dev/gpiochip0`) is required which is toggled by RepRapFirmware whenever the firmware is ready to exchange data.

More technical documentation about this can be found [here](https://chrishamm.github.io/DuetSoftwareFramework/api/DuetControlServer.SPI.Communication.html).

### Inter-Process Communication

DuetControlServer provides a UNIX socket for inter-process commmunication. This socket usually resides in `/var/run/dsf/dcs.sock` .
For .NET Core, DSF provides the `DuetAPIClient` class library which is also used internally by the DSF core applications.

Technical information about the way the communication over the UNIX socket works can be found in the [API description](#api).

### Machine Model

DSF provides a central machine model that is supposed to replicate the entire machine state. This machine model is also synchronized with Duet Web Control and stored in its backend.

DuetControlServer provides several functions for synchronized access along with some primitives to modify certain values on demand.

## DuetWebServer

This application provides Duet Web Control along with a RESTful API and possibly custom HTTP endpoints.
It is implemented using ASP.NET Core and uses Kestrel internally. The coniguration file defaults to `/opt/dsf/conf/http.json`.

### Configuration

The configuration file of DuetWebServer can be found in `/opt/dsf/conf/http.json`. In this file various settings can be tuned.

In the `Logging` section, the default minimum `LogLevel` can be changed to one of `Trace`, `Debug`, `Information`, `Warning`, `Error`, or`Critical`. It defaults to `Information`.

The `Kestrel` section specifies the default configuration of the underlying webserver. Further details about the available options can be found at [Microsoft Docs](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel?view=aspnetcore-3.1#kestrel-options).

Apart from these two sections, you can also customize the following settings:

- `KeepAliveInterval`: Default keep-alive interval for WebSocket connections. This is useful if DWS is operating as a reverse proxy
- `ModelRetryDelay`: If DuetControlServer is not running, this specifies the delay between reconnect attempts in milliseconds
- `ObjectModelUpdateTimeout`: When a WebSocket is connected and waiting for object model changes, this specifies the timeout after which DWS stops waiting and polls the WebSocket again
- `UseStaticFiles`: Whether to provide web files from the virtual `www` directory. This is required for DWC if DWS is not running as a reverse proxy
- `DefaultWebDirectory`: Default web directory to fall back to if DCS could not be contacted (requires `UseStaticFiles` to be set)
- `MaxAge`: Maximum cache time for static files (requires `UseStaticFiles` to be true)
- `WebSocketBufferSize`: This defines the maximum buffer size per third-party WebSocket connection

It is possible to override these settings using command-line arguments, too.

### Operation as a reverse proxy

If you wish to use another HTTP server than DuetWebServer, it is possible to set up DuetWebServer as a reverse proxy.
Check out [this](https://docs.microsoft.com/en-us/aspnet/core/host-and-deploy/linux-apache?view=aspnetcore-3.1) page for Apache and [this](https://docs.microsoft.com/en-us/aspnet/core/host-and-deploy/linux-nginx?view=aspnetcore-3.1) one for nginx.

### REST API

DuetWebServer provides a RESTful API that is primarily targeted at Duet Web Control.
In the following these endpoints are described. Note that these endpoints differ from those provided by RepRapFirmware's native network interface.

#### WS /machine

WebSocket request for object model subscriptions. When a client connects, the web server registers a new HTTP user session in the object model.
After that, the whole object model is sent from the web server to a client in JSON format. When the client has processed this response, it responds by sending `OK\n` (\n is NL) to acknowledge the receipt.
When further object model changes are detected, the web server sends a JSON object (patch) representing the changes in the object model back to the client so that it can update the object model again.
Note that these changes are only returned as a *partial* machine model which may not be confused with a *JSON Patch* as defined by proposed [RFC 6902](https://tools.ietf.org/html/rfc6902)).
To see this behaviour in action (except for the first full model transfer), check out the [ModelObserver](#ModelObserver).

If the client does not receive an update for a while, it may send a `PING\n` message to the server. When the server receives this message, it responds with `PONG\n` at last after the time specified via `ObjectModelUpdateTimeout`.
Although the WebSocket server implements a server-side keep-alive interval, this mechanism is useful for clients to detect unexpected connection aborts (e.g. when the machine is suddenly powered off).

If the query is no WebSocket, HTTP code `400` is returned.

#### GET /machine/status

Query the full machine model. The model is returned as a JSON object.

Returns one of these HTTP status codes:

- `200`: Query OK, current machine model is returned as `application/json`
- `500`: Generic error
- `502`: Incompatible DCS version
- `503`: DCS is unavailable

#### POST /machine/code

Execute plain G/M/T-code(s) from the raw request body and return the G-code response when done.

Returns one of these HTTP status codes:

- `200`: Code(s) have finished, reply is returned as `text/plain`
- `500`: Generic error
- `502`: Incompatible DCS version
- `503`: DCS is unavailable

#### GET /machine/file/\{filename\}

Download a file. The file path is translated to a physical file path.

Returns one of these HTTP status codes:

- `200`: File content
- `404`: File not found
- `500`: Generic error
- `502`: Incompatible DCS version
- `503`: DCS is unavailable

#### PUT /machine/file/\{filename\}

Upload a file. The file path is translated to a physical file path. The body payload is the file content.

Returns one of these HTTP status codes:

- `201`: File created
- `500`: Generic error
- `502`: Incompatible DCS version
- `503`: DCS is unavailable

#### GET /machine/fileinfo/\{filename\}

Parse a given G-code file and return information about this job file as a JSON object.
See [API Documentation](https://chrishamm.github.io/DuetSoftwareFramework/api/DuetAPI.ParsedFileInfo.html) for further information about the object returned.

Returns one of these HTTP status codes:

- `200`: Parsed file information as `application/json`
- `500`: Generic error
- `502`: Incompatible DCS version
- `503`: DCS is unavailable

#### DELETE /machine/file/\{filename\}

Delete a file or directory. The file path is translated to a physical file path.

Returns one of these HTTP status codes:

- `204`: File or directory succesfully deleted
- `404`: File or directory not found
- `500`: Generic error
- `502`: Incompatible DCS version
- `503`: DCS is unavailable

#### POST /machine/file/move

Move a file or directory from a to b. The file paths are translated to physical file paths.
This query uses the following parameters as an `application/x-www-form-urlencoded`:

- `from`: Source file path
- `to`: Destination file path
- `force` (optional): If the destination file already exists, delete it first. Defaults to false

Returns one of these HTTP status codes:

- `204`: File or directory succesfully moved
- `404`: File or directory not found
- `500`: Generic error
- `502`: Incompatible DCS version
- `503`: DCS is unavailable

#### GET /machine/directory/\{directory\}

Get a file list of the given directory. The directory path is translated to a physical file path.

The format of the returned file list is a JSON array and it looks like:

```
[
    {
        type: 'd',
        name: 'sys',
        date: '2019-10-21T23:47:59+01:00'
    },
    {
        type: 'f',
        name: 'some file.gcode',
        size: 23534,
        date: '2019-10-21T23:47:59+01:00'
    }
]
```

`type` can be either `d` if the item is a directory or `f` if it is a file. `size` is always the number of bytes.

#### PUT /machine/directory/\{directory\}

Create the given directory. The directory path is translated to a physical directory path.

Returns one of these HTTP status codes:

- `201`: Directory created
- `500`: Generic error
- `502`: Incompatible DCS version
- `503`: DCS is unavailable

#### PUT /machine/plugin

Install or upgrade a plugin ZIP file. The body payload is the ZIP file content.

Returns one of these HTTP status codes:

- `204`: Plugin has been installed
- `500`: Generic error
- `502`: Incompatible DCS version
- `503`: DCS is unavailable

#### DELETE /machine/plugin

Uninstall a plugin. The body payload is the name of the plugin to remove.

Returns one of these HTTP status codes:

- `204`: Plugin has been uninstalled
- `500`: Generic error
- `502`: Incompatible DCS version
- `503`: DCS is unavailable

#### PATCH /machine/plugin

Set plugin data in the object model if there is no SBC executable. The body payload is JSON in the format

```
{
    "plugin": "<PluginName>",
    "key": "<Key>",
    "value": <JSON value>
}
```

If there is an SBC executable, expose your own HTTP endpoints to modify shared plugin data.

Returns one of these HTTP status codes:

- `204`: Data has been set
- `500`: Generic error
- `502`: Incompatible DCS version
- `503`: DCS is unavailable

#### POST /machine/startPlugin

Start a plugin on the SBC. The body payload is the name of the plugin to start.
This does nothing if a plugin has already been started.

- `204`: Plugin has been started
- `500`: Generic error
- `502`: Incompatible DCS version
- `503`: DCS is unavailable

#### POST /machine/stopPlugin

Stop a plugin on the SBC. The body payload is the name of the plugin to stop.
This does nothing if a plugin has already been stopped.

- `204`: Plugin has been started
- `500`: Generic error
- `502`: Incompatible DCS version
- `503`: DCS is unavailable

## Tools

DSF comes with a bunch of command-line tools. They may be invoked from a local terminal or from a remote connection via SSH.
All these tools are installed to `/opt/dsf/bin` so e.g. the CodeConsole utility can be invoked using `/opt/dsf/bin/CodeConsole`.

### CodeConsole

This tool can be used to run G/M/T-codes and to wait for a result.

#### Command-Line Options

The following command-line arguments are available:

- `-s`, `--socket`:  Specify the UNIX socket to connect to. Defaults to `/var/run/dsf/dcs.sock`
- `-c`, `--code`: Execute the given code(s), wait for the result and exit
- `-q`, `--quiet`: Do not display when a connection has been established (only applicable if `-c` is not set)
- `-h`, `--help`: Display all available command-line parameters

### CodeLogger

This tool lets you track the lifecycle of a G/M/T-code that is processed by DSF.
Once launched, these types of code interception can be observed:

- `pre`: Code is being started but it has not been processed internally yet
- `post`: Code has been executed internally but it has not been resolved. It is about to be sent to RepRapFirmware for further processing
- `executed`: Code has been executed and a result is about to be returned

#### Command-Line Options

The following command-line arguments are available:

- `-s`, `--socket`:  Specify the UNIX socket to connect to. Defaults to `/var/run/dsf/dcs.sock`
- `-q`, `--quiet`: Do not display when a connection has been established (only applicable if `-c` is not set)
- `-h`, `--help`: Display all available command-line parameters

### CustomHttpEndpoint

This tool lets you create a custom RESTful HTTP or WebSocket endpoint via `/machine/{namespace}/{path}`. If started without any command-line arguments, it will try to register a new HTTP GET endpoint at `/machine/custom-http-endpoint/demo` that is accessible from a web browser. It is possible to register different HTTP methods at the same endpoint path.

By default, this tool echoes the session ID, HTTP method, received HTTP headers, queries, and body (if set).
If WebSocket mode is selected, the tool will wait for the a single connection, print text data received from the WebSocket to stdout and send input lines from stdin back to the WebSocket.

Binary data transfers are not supported in any mode. If you need to transfer binary data, upload to a file instead and call your custom endpoint when the transfer has finished.

#### Command-Line Options

- `-s`, `--socket`:  Specify the UNIX socket to connect to. Defaults to `/var/run/dsf/dcs.sock`
- `-m`, `--method`: Set designated method for the HTTP endpoint. May be one of `GET`, `POST`, `PUT`, `PATCH`, `TRACE`, `DELETE`, `OPTIONS`, `WebSocket`
- `-n`, `--namespace`: Set namespace to use (defaults to custom-http-endpoint)
- `-p`, `--path`: Set HTTP query path (defaults to demo)
- `-e`, `--exec`: Set binary file to execute when an HTTP query is received. Stdout and stderr are returned as the response body once the program terminates
- `-a`, `--args`: Set arguments for the executable file. Query values in `%` characters are replaced with query options (e.g. `%myvalue%`). Not applicable for WebSockets
- `-q`, `--quiet`: Do not display info text
- `-h`, `--help`: Display all available command-line parameters

### ModelObserver

This tool lets you keep track of object model changes.
Since it relies on a [model subscription](#model-subscriptions), it gives an idea how model updates are pushed from DuetWebServer to Duet Web Control.

#### Command-Line Options

- `-s`, `--socket`:  Specify the UNIX socket to connect to. Defaults to `/var/run/dsf/dcs.sock`
- `-h`, `--help`: Display all available command-line parameters

## Building

### 3. Building things on your own

Of course you can build everything on your own, too. In order to build packages like those on the package feed, check out the `build.sh` script in the `pkg` directory.
If you wish to make changes to the existing software and to test it, you need to get the [.NET Core 3.x SDK](https://dotnet.microsoft.com/download/dotnet-core) first.

#### 3.1 Building on a remote system

Every .NET Core application of DSF is references the `DotnetPublishSsh` package which allows you to compile and upload .NET Core applications for ARMv7/AArch64.
In order to use this, it is recommended to enable remote `root` access first.
To do so, open `/etc/ssh/sshd_config` with an editor of your choice, look for the line

```
#PermitRootLogin prohibit-password
```

and replace it with

```
PermitRootLogin yes
```

Once done, restart the SSH daemon and change the root password to e.g. `raspberry`:

```
sudo systemctl restart sshd
sudo passwd
```

Then make sure to shut down all the DSF components before you continue, else you may get errors:

```
sudo systemctl stop duetcontrolserver
sudo systemctl stop duetwebserver
```

After that, open a new local console in one of the DSF application directories (where a .csproj file lies) and run:

```
dotnet publish-ssh -r linux-arm --ssh-host duet3 --ssh-user root --ssh-password raspberry --ssh-path /opt/dsf/bin /p:DefineConstants=VERIFY_OBJECT_MODEL
```

This will replace the stock DSF component with your own compiled variant.
If you do not wish to publish everything to your board at the time of compiling, have a look at the `dotnet publish` command.

### 3.2 Building on the SBC itself

Of course you can compile the required components on the SBC itself. Once the .NET Core SDK has been installed, enter the directory of the DSF application you want to compile and run `dotnet build`. This will generate suitable binaries for you.

## API

DSF provides a powerful API aimed at expandability and flexibility. The easiest way to get started with it is to obtain the .NET Core package called `DuetAPIClient`.
Both the API and the API client are available as nuget packages (see [here](https://www.nuget.org/packages/DuetAPI) and [here](https://www.nuget.org/packages/DuetAPIClient)).
To get a basic idea how the .NET Core-based DuetAPIClient works, check out the source code of the [CodeConsole utility](src/CodeConsole/Program.cs) and the [code documentation](https://chrishamm.github.io/DuetSoftwareFramework/api/DuetAPIClient.html).

The .NET-based API libraries are - unlike the other DSF components - licensed under the terms of the LGPL 3.0 or later.
If you wish to build your own API client, it is strongly recommended to follow the DuetAPIClient implementation because it properly documents and handles possible exceptions of every command.

Note that DSF 3.2 requires permissions for third-party plugins that are installed from the web interface. Please see the [plugins](PLUGINS.md) documentation for further details.

### Inter-Process Communication

To make use of the API, inter-process communication has to be performed. For this purpose UNIX sockets are used.
By default, DSF provides a UNIX socket at `/var/run/dsf/dcs.sock` which other applications can connect to.

A third-party application may connect to this socket in stream mode.
Once a connection has been established, DCS sends a welcome message in JSON format like

```
{
    "id": 12,
    "version": 3
}
```

where `id` represents a connection identifier and `version` the version number of the API provided.
Once this object has been received, the client is supposed to either close the connection (e.g. because the API level is incompatible) or to send back an init message with the desired mode of operation:

```
{
    "mode": "Command"
}
```

where `mode` can be either `Command`, `Intercept`, or `Subscribe` (see [docs](https://chrishamm.github.io/DuetSoftwareFramework/api/DuetAPI.Connection.ConnectionMode.html)). This is then acknowledged by the server:

```
{
    "success": true
}
```

If anything went wrong during the mode selection, the server may respond with

```
{
    "success": false,
    "errorType": "ArgumentException",
    "errorMessage": "Invalid connection mode"
}
```

If you are using the DuetAPIClient, there is no need to deal with plain JSON objects yourself. The API libraries already provide wrappers for every supported object type.
To see how JSON objects are exchanged in detail, you can start DuetControlServer with the `--log-level trace` option.

#### Command Mode

A command connection is a universal connection mode in which various commands can be performed.
Check out the [code documentation](https://chrishamm.github.io/DuetSoftwareFramework/api/DuetAPIClient.BaseCommandConnection.html#methods) for a list of supported commands.

To put the new connection into `Command` mode, a client can respond to the first init message with

```
{
    "mode": "Command"
}
```

Once this mode has been selected and the success response as described above has been processed by the client, the client may issue [supported commands](https://chrishamm.github.io/DuetSoftwareFramework/api/DuetAPIClient.BaseCommandConnection.html#methods).
For example:

```
{
    "command": "SimpleCode",
    "code": "G4 S3"
}
```

As soon as `G4 S3` completes, a response like

```
{
    "success": true,
    "result": ""
}
```

is sent back to the client. Here the `result` field holds the final code result. Note that the connection will be blocked as long as a command is being processed.

#### Interception Mode

Duet Software Framework allows you to intercept codes as they are being processed. At present, the following interception modes are supported:

- `pre`: Code is being started but it has not been processed internally yet
- `post`: Code has been executed internally but it has not been resolved. It is about to be sent to RepRapFirmware for further processing
- `executed`: Code has been executed and a result is about to be returned

Once the server's initialization message has been received, a client may choose to intercept codes by sending

```
{
    "mode": "Intercept",
    "interceptionMode": "Pre"
}
```

As in the example above, this is acknowledged by the server:

```
{
    "success": true
}
```

After this the client has to listen for incoming codes until the connection has been closed. When a code is being executed, something like this is sent to the client:

```
{
    "type": 'G',
    "parameters": [
        "letter": 'S',
        "value": "4",
        "isString": "false"
    ],
    ...
}
```

See the [documentation](https://chrishamm.github.io/DuetSoftwareFramework/api/DuetAPI.Commands.Code.html#properties) for further information about the transmitted fields.

After receipt, the code can be either:

- [ignored](https://chrishamm.github.io/DuetSoftwareFramework/api/DuetAPI.Commands.Ignore.html) which lets the code continue as expected,
- [canceled](https://chrishamm.github.io/DuetSoftwareFramework/api/DuetAPI.Commands.Ignore.html) which cancels the code and throws an `OperationCanceledException` on its task,
- [resolved](https://chrishamm.github.io/DuetSoftwareFramework/api/DuetAPI.Commands.Resolve.html) which resolves the code using a given result

Apart from that, you can run any commands like in the [command mode](#command-mode) while a code is being intercepted.
Note that it is mandatory to send one of the three commands above to avoid deadlocks. If the connection is interrupted while a code is being intercepted, this happens automatically in the background.

So if a client wants to ignore the received code, it can send

```
{
    "command": "Ignore"
}
```

After that, DuetControlServer will send the next code in the queue to the client.

#### Subscription Mode

In many use-cases a third-party application wishes to receive updates about the [machine model](https://chrishamm.github.io/DuetSoftwareFramework/api/DuetAPI.Machine.MachineModel.html#properties), which contains information like axis positions and other useful information.
For this purpose Duet Software Framework provides a mode in which machine model updates can be received whenever the machine model has been updated.

In this mode, only a single command is supported: [Acknowledge](https://chrishamm.github.io/DuetSoftwareFramework/api/DuetAPI.Commands.Acknowledge.html)
Send this command whenever the latest machine model update has been processed. Any other command will create an `ArgumentException`.
Note that the `Acknowledge` command does NOT send back a standard response; instead the next model update is transferred as soon as it is available.

This mode provides [two types of operation](https://chrishamm.github.io/DuetSoftwareFramework/api/DuetAPI.Connection.SubscriptionMode.html):

1. Full mode. Every time the machine model has been updated and parsed, the whole machine model is serialized and send to the API client
2. Patch mode. In this mode, only the changed object model fields are transferred when an update occurs. In addition, you can specify a [filter](https://chrishamm.github.io/DuetSoftwareFramework/api/DuetAPI.Connection.InitMessages.SubscribeInitMessage.html#DuetAPI_Connection_InitMessages_SubscribeInitMessage_Filter) in case you are only interested in a certain namespace.

Regardless of the chosen mode, the first thing the client will receive is the full serialized machine model.
In case a client wants to connect, an init message like this has to be sent first:

```
{
    "mode": "Subscribe",
    "subscriptionMode": "Patch"
}
```

Like in the examples before the server responds with a success message:

```
{
    "success": true
}
```

Which is followed by the full serialized machine model:

```
{
    "channels:" { ... },
    "electronics": { ... },
    ...
}
```

In order to flag the readiness for processing more data, the client sends back

```
{
    "command: "Acknowledge"
}
```

After that, the (partial) machine model is sent back to the client when needed.

### Custom HTTP endpoints

For custom applications, it can be handy to register custom HTTP endpoints. For this purpose, there is a [command](https://chrishamm.github.io/DuetSoftwareFramework/api/DuetAPI.Commands.AddHttpEndpoint.html#properties) that lets you create one.
However, these endpoints only support plain text and no binary data. Consider this when designing your own API endpoints.

#### Custom RESTful HTTP endpoints

To create a new HTTP REST endpoint, one may send

```
{
    "command": "AddHttpEndpoint",
    "endpointType": "GET",
    "namespace": "third-party-app",
    "path", "test",
    "uploadRequest": false
}
```

which creates a response from DuetControlServer like

```
{
    "success": true,
    "result": "/var/run/dsf/third-party-app/test-GET.sock"
}
```

where the `result` represents the path to the UNIX socket that DWS will use. As a consequence, the third-pary application has to start listening on the specified UNIX socket path in `SOCK_STREAM` mode.
Whenever a new HTTP request is made, DuetWebServer will attempt to connect to the given UNIX socket path. After that, a serialized [ReceivedHttpRequest](https://chrishamm.github.io/DuetSoftwareFramework/api/DuetAPI.Commands.ReceivedHttpRequest.html#properties) JSON object is sent.
The endpoint server may then send back a serialized [SendHttpResponse](https://chrishamm.github.io/DuetSoftwareFramework/api/DuetAPI.Commands.SendHttpResponse.html#properties) JSON object that specifies what result needs to be returned to the client.

#### Custom WebSockets

If you want to create your own custom WebSocket provider, replace `GET` with `WebSocket` above.
When a new WebSocket is connected, DuetWebServer will attempt to connect to the same UNIX socket. However, a client may not have sent any information yet and in this case no `ReceivedHttpRequest` response is sent to the UNIX socket provider.
To send a message to the WebSocket, send the same `SendHttpResponse` over the UNIX socket connection.

For further information, check the documentation or have a look at the [CustomHttpEndpoint source code](src/CustomHttpEndpoint/Program.cs).

### API clients for other programming languages

The following third-party bindings are available:

- Go: https://github.com/wilriker/goduetapiclient

## Unit Tests

To ensure flawless operation of the most critical components, Duet Software Framework relies on unit tests via the NUnit framework. These unit tests can be found in the [src/UnitTests](/src/UnitTests) directory.

## Known incompatibilities

- `killall` may not be used to terminate DuetControlServer. Since it sends SIGTERM to all processes including worker threads of the .NET task scheduler, an abnormal program termination is the consequence. It is better to send SIGTERM only to the main PID
- G-Code checksums and M998 are not supported

## Reporting issues

Before reporting any new issues, please check if it is already a known issue or if it has been already reported on the [GitHub issues](https://github.com/chrishamm/DuetSoftwareFramework/issues) page.
For general support questions, please check out the [forums](https://forum.duet3d.com/category/31/dsf-development).

If the issue you encountered is easy to reproduce, please file a new issue in the GitHub issues page along with instructions on how to reproduce.

If the issue only happens sporadically, please launch DuetControlServer with the log level `debug` and provide the log including a short note when the error occurred.
To launch DuetControlServer with this log level on DuetPi, you may run the following commands from an SSH terminal:

```
sudo systemctl stop duetcontrolserver
sudo /opt/dsf/bin/DuetControlServer -l debug -r
```
