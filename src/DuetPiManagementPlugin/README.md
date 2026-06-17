# DuetPiManagementPlugin

`DuetPiManagementPlugin` is the bundled privileged plugin that gives a DuetPi system a firmware-style way to manage the SBC itself. It intercepts selected M-codes and turns them into persistent Linux-side operations such as network reconfiguration, hostname changes, storage mounting, reboot, and shutdown.

## At A Glance

| Aspect | Details |
|---|---|
| Entry point | [Program.cs](Program.cs) |
| Runtime type | DSF plugin executable |
| Key areas | [Command.cs](Command.cs), [Mount.cs](Mount.cs), [Network/](Network) |
| Primary dependencies | [../DuetAPI/README.md](../DuetAPI/README.md), [../DuetAPIClient/README.md](../DuetAPIClient/README.md), [../DuetSharedLibrary/README.md](../DuetSharedLibrary/README.md) |

## What This Plugin Does

It provides SBC-management functions through familiar M-codes so the user experience on DuetPi stays close to standalone firmware behavior. The plugin is intended for DSF systems that need to configure the Linux host from the printer control plane.

Supported codes include:

- `M21`: mount storage
- `M22`: unmount storage
- `M540`: set MAC address
- `M550`: set machine name / hostname integration
- `M552`: configure IP address and enable or disable interfaces
- `M553`: set netmask
- `M554`: set gateway
- `M586`: configure network protocols
- `M587`: manage remembered WiFi networks
- `M588`: forget a remembered WiFi network
- `M589`: configure access-point parameters
- `M905`: set the RTC date and time
- `M999 B-1`: reboot the SBC
- `M999 B-1 P"OFF"`: power down the SBC

## How It Works

The plugin runs as a DSF plugin process and communicates with DCS over the normal IPC socket. It resolves supported M-codes before they need to reach firmware, then applies the corresponding Linux-side configuration changes.

The implementation is split broadly into:

- [Command.cs](Command.cs) for code interception and command handling;
- [Mount.cs](Mount.cs) for mount and unmount operations;
- [Network/](Network) for interface management, DHCP/static address handling, WiFi scanning, access-point setup, and protocol configuration;
- [JsonContext.cs](JsonContext.cs) for the plugin's JSON serialization helpers.

Several operations write persistent configuration rather than temporary runtime state. For example, HTTP and HTTPS protocol management updates DSF-side web configuration files such as `/opt/dsf/conf/http.json` and may create `/opt/dsf/conf/https.pfx`.

## Interfaces With Other DSF Projects

| Peer | Interface |
|---|---|
| [../DuetControlServer/README.md](../DuetControlServer/README.md) | DCS hosts the code pipeline where this plugin intercepts and resolves its supported M-codes. |
| [../DuetPluginService/README.md](../DuetPluginService/README.md) | The root plugin-service instance installs and launches this plugin because it needs elevated permissions. |
| [../DuetWebServer/README.md](../DuetWebServer/README.md) | Some settings modified here affect DWS behavior, especially network protocol and certificate configuration. |

## Relationship To RepRapFirmware

This plugin exists specifically to bridge the gap between firmware-era control commands and SBC-hosted functionality. It does not talk to RepRapFirmware directly; instead it intercepts selected codes on the SBC side and resolves them inside DSF so the Linux host can perform the requested system-management action.

That means the semantics are intentionally similar to standalone mode, but the effect is often more persistent because the underlying state lives in Linux configuration files and services.

## Requirements

To use the full feature set, the surrounding Linux system is expected to provide packages such as:

- `openssl`
- `proftpd`
- `ssh`
- `telnetd`
- `dnsmasq`
- `hostapd`
- `wpa_supplicant`

## Build And Package

```sh
dotnet publish -r linux-arm -o ./zip/dsf /p:PublishTrimmed=true
```

Package the contents of `zip/` into a plugin ZIP for installation. Because this is a privileged plugin, third-party root-plugin support must be enabled first in DCS configuration by setting `RootPluginSupport` in `/opt/dsf/conf/config.json`.

## Notes And Limitations

- Changes made by this plugin are persistent on the SBC and should be treated as system configuration, not temporary print-session state.
- `M586 P2 R` cannot change the Telnet port. That still requires editing `/etc/inetd.conf` manually.
- Unless NetworkManager is in use, `M587` does not preserve per-SSID IP configuration; the configured address behavior applies more broadly.

## Related Docs

- [../../docs/devel/PLUGINS.md](../../docs/devel/PLUGINS.md)
- [../DuetPluginService/README.md](../DuetPluginService/README.md)
- [../DuetControlServer/README.md](../DuetControlServer/README.md)
