# Duet Software Framework

![Version](https://img.shields.io/github/v/release/Duet3D/DuetSoftwareFramework) ![License](https://img.shields.io/github/license/Duet3D/DuetSoftwareFramework?color=blue) ![Issues](https://img.shields.io/github/issues/Duet3D/DuetSoftwareFramework?color=blue)

Duet Software Framework resembles a collection of programs to control an attached Duet3D board from a Linux-based mini computer (SBC). Since it is using .NET, it requires an ARM processor that supports ARMv7 instructions processor is required (Raspberry Pi 2 or newer).

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

### Return Codes

This application may return the following codes (derived from `sysexits.h`):
- `0`: Successful termination
- `64`: Failed to initialize settings (usage error)
- `69`: Could not connect to Duet (service unavailable)
- `70`: Internal software error
- `71`: Failed to initialize environment (OS error)
- `73`: Failed to initialize IPC socket (Cannot create file)
- `74`: Could not open SPI or GPIO device (IO error)
- `75`: Auto-update disabled or other instance already running (temporary failure)
- `78`: Bad settings file (configuration error)

### SPI Link

In order to connect to the firmware, a binary data protocol is used. DuetControlServer attaches to the Duet using an SPI connection (typically `/dev/spidev0.0`) in master mode.
In addition, a GPIO pin (typically pin 22 on the Raspberry Pi header via `/dev/gpiochip0`) is required which is toggled by RepRapFirmware whenever the firmware is ready to exchange data.

More technical documentation about this can be found [here](https://duet3d.github.io/DuetSoftwareFramework/api/DuetControlServer.SPI.Communication.html).

### Inter-Process Communication

DuetControlServer provides a UNIX socket for inter-process commmunication. This socket usually resides in `/var/run/dsf/dcs.sock` .
For .NET, DSF provides the `DuetAPIClient` class library which is also used internally by the DSF core applications.

Technical information about the way the communication over the UNIX socket works can be found in the [API Overview](https://github.com/Duet3D/DuetSoftwareFramework/wiki/API-Overview).

### Object Model

Like RepRapFirmware, DSF provides a central object model that replicates the entire machine state.
This model data is synchronized with Duet Web Control as well and stored in its backend.
For further information about the object model structure, check out the [DSF code documentation](https://duet3d.github.io/DuetSoftwareFramework/api/DuetAPI.ObjectModel.ObjectModel.html#properties).

### Virtual SD Card

Since RepRapFirmware relies on files from a SD card, DSF provides an emulation layer.
These files are stored inside a "virtual SD card" for RepRapFirmware in `/opt/dsf/sd`.
However, this path may be adjusted in the `config.json` file (in `/opt/dsf/conf`).

### Configuration

The configuration behind DCS is kept quite flexible in order to allow for simple changes.
By default those values are stored in `/opt/dsf/conf/config.json` but the config path may be overridden using a command-line parameter.

Check out the [code documentation](https://duet3d.github.io/DuetSoftwareFramework/api/DuetControlServer.Settings.html#properties) for an overview of the available settings.

## DuetWebServer

This application provides Duet Web Control along with a RESTful API and possibly custom HTTP endpoints.
It is implemented using ASP.NET and uses Kestrel internally. The coniguration file defaults to `/opt/dsf/conf/http.json`.

### Configuration

The configuration file of DuetWebServer can be found in `/opt/dsf/conf/http.json`. In this file various settings can be tuned.

In the `Logging` section, the default minimum `LogLevel` can be changed to one of `Trace`, `Debug`, `Information`, `Warning`, `Error`, or`Critical`. It defaults to `Information`.

The `Kestrel` section specifies the default configuration of the underlying webserver. Further details about the available options can be found at [Microsoft Docs](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel?view=aspnetcore-3.1#kestrel-options).

Apart from these two sections, you can also customize the following settings:

- `SocketPath`: Path to the UNIX socket provided by DCS
- `StartErrorFile`: Optional file containing the last start error from DCS
- `KeepAliveInterval`: Default keep-alive interval for WebSocket connections. This is useful if DWS is operating as a reverse proxy
- `SessionTimeout`: Default timeout for inactive HTTP sessions
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

### CodeStream

This tool can be used to stream codes in order to execute multiple G/M/T-codes without waiting for a response first.
When a code has finished, the corresponding result is written to the console.

#### Command-Line Options

The following command-line arguments are available:

- `-s`, `--socket`:  Specify the UNIX socket to connect to. Defaults to `/var/run/dsf/dcs.sock`
- `-b`, `--buffer-size <size>`: Maximum number of codes to execute simultaneously
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

### DuetPiManagementPlugin

This DSF plugin provides various M-code extensions to mimic standalone compatibility using various M-codes.
It is intended to be used with DuetPi because it requires various services to be installed first.

See its dedicate [README.md](src/DuetPiManagementPlugin/README.md) for further details.

#### Command-Line Options

- `-s`, `--socket`:  Specify the UNIX socket to connect to. Defaults to `/var/run/dsf/dcs.sock`

### DuetPluginService

Two instances of this service run as

1. DSF user
2. Root user

It is in charge of plugin installations, security profiles, and plugin lifecycles.
Hence this service starts and stops whenever DuetControlServer does (using the `PartOf` systemd binding).

Unlike DuetControlServer, which starts early in the boot process (`sysinit` target), this service is started as soon as the system reaches the `multi-user` target.
This is intended to reduce load on the system during start-up and to make sure potentially required resources are available when third-party plugins are started.

As soon as both plugin services (and the previously started plugins) have been started, `dsf-config.g` is executed by DuetControlServer.
Note that `dsf-config.g` does not run if not both services have been started unless plugin support has been disabled in `/opt/dsf/conf/config.json`.

### Command-Line Options

- `-l`, `--log-level`: Set the minimum log level. Valid options are: `trace`, `debug` , `info` , `warn`, `error`, `fatal`, `off` Default is `info`
- `-c`, `--config`: Override the path to the JSON configuration file. Defaults to `/opt/dsf/conf/config.json`
- `-s`, `--socket`:  Specify the UNIX socket to connect to. Defaults to `/var/run/dsf/dcs.sock`
- `-h`, `--help`: Display all available command-line parameters

### ModelObserver

This tool lets you keep track of object model changes.
Since it relies on a [model subscription](#model-subscriptions), it gives an idea how model updates are pushed from DuetWebServer to Duet Web Control.

#### Command-Line Options

- `-s`, `--socket`:  Specify the UNIX socket to connect to. Defaults to `/var/run/dsf/dcs.sock`
- `-f`, `--filter <filter>`: UNIX socket to connect to
- `-c`, `--confirm`: Confirm every JSON receipt manually
- `-q`, `--quiet`: Do not display when a connection has been established
- `-h`, `--help`: Display all available command-line parameters

### PluginManager

This tool is intended to manage third-party plugins directly on the SBC without a GUI.

#### Command-Line Options

- `-s`, `--socket`:  Specify the UNIX socket to connect to. Defaults to `/var/run/dsf/dcs.sock`
- `list`: List installed plugins
- `list-data`: List plugin data of all installed plugins
- `install <pluginZIP>`: Install a plugin ZIP file
- `start <plugin>`: Start a plugin
- `set-data <plugin>:<key>=<value>`: Set plugin data 
- `stop <plugin>`: Stop a plugin
- `uninstall <plugin>`: Uninstall a plugin
- `-h`, `--help`: Display all available command-line parameters

## Unit Tests

To ensure flawless operation of the most critical components, Duet Software Framework relies on unit tests via the NUnit framework. These unit tests can be found in the [src/UnitTests](src/UnitTests) directory.

## Known incompatibilities

- G-Code checksums and M998 are not supported
- `exists()` may be used in meta G-code expressions only for fields that are managed by RRF

## Reporting issues

Before reporting any new issues, please check if it is already a known issue or if it has been already reported on the [GitHub issues](https://github.com/duet3d/DuetSoftwareFramework/issues) page.
For general support questions, please check out the [forums](https://forum.duet3d.com/category/31/dsf-development).

If the issue you encountered is easy to reproduce, please file a new issue in the GitHub issues page along with instructions on how to reproduce.

If the issue only happens sporadically, please launch DuetControlServer with the log level `debug` and provide the log including a short note when the error occurred.
To launch DuetControlServer with this log level on DuetPi, you may run the following commands from an SSH terminal:

```
sudo systemctl stop duetcontrolserver
sudo /opt/dsf/bin/DuetControlServer -l debug -r
```
