# DuetPluginService

`DuetPluginService` is the DSF daemon that installs, validates, starts, stops, and removes plugins. It runs as both the regular DSF user and as `root`, so DSF can keep most plugins constrained while still allowing a small number of privileged plugins when explicitly enabled.

## At A Glance

| Aspect | Details |
|---|---|
| Entry point | [Program.cs](Program.cs) |
| Runtime type | Long-running console daemon, typically started twice by systemd |
| Main config | `/opt/dsf/conf/plugins.json` via [Settings.cs](Settings.cs) |
| Key areas | [PluginService.cs](PluginService.cs), [PluginStore.cs](PluginStore.cs), [Commands/](Commands), [IPC/](IPC), [PermissionManagers/](PermissionManagers) |

## What This Project Owns

- Plugin installation and removal from `/opt/dsf/plugins`.
- Manifest reload, start, stop, and status operations.
- AppArmor profile generation and enforcement for non-root plugins.
- Package-install hooks for plugin dependencies, including apt, dpkg, and Python package helpers.
- The process supervision layer that launches plugin executables under the correct security context.

## How It Works

[Program.cs](Program.cs) loads [Settings.cs](Settings.cs), configures logging, and connects to DCS using the dedicated plugin-service IPC mode implemented in [IPC/PluginServiceConnection.cs](IPC/PluginServiceConnection.cs).

The core flow is:

1. [PluginStore.cs](PluginStore.cs) discovers installed plugin manifests and keeps their current state.
2. Commands in [Commands/](Commands) handle install, reload, start, stop, uninstall, and package-management requests.
3. [PermissionManagers/AppArmorPermissionManager.cs](PermissionManagers/AppArmorPermissionManager.cs) materializes the security profile needed for normal plugins.
4. [PluginService.cs](PluginService.cs) launches or terminates plugin processes and reports state back to DCS.
5. The privileged instance handles plugins that request `SbcPermissions.SuperUser`; the unprivileged instance handles the rest.

## Interfaces With Other DSF Projects

| Peer | Interface |
|---|---|
| [../DuetControlServer/README.md](../DuetControlServer/README.md) | DCS remains the client-visible control plane and forwards plugin commands to the appropriate plugin-service instance. |
| [../DuetAPI/README.md](../DuetAPI/README.md) | Supplies plugin manifests, permissions, and command contracts. |
| [../DuetAPIClient/README.md](../DuetAPIClient/README.md) | Provides the IPC client used for service-to-service communication. |
| [../PluginManager/README.md](../PluginManager/README.md) | CLI front end that ultimately drives DCS and this service. |
| Third-party plugins | Are launched, supervised, and sandboxed by this service. |

## Relationship To RepRapFirmware

`DuetPluginService` does not talk directly to RepRapFirmware. Plugins typically influence firmware behavior indirectly by intercepting codes, sending DCS commands, or registering HTTP endpoints. DCS remains the single firmware-facing process.

## Runtime Inputs And Outputs

- Configuration: `/opt/dsf/conf/plugins.json`
- Plugin directory: `/opt/dsf/plugins`
- AppArmor template: `/opt/dsf/conf/apparmor.conf`
- AppArmor profile directory: `/etc/apparmor.d`
- DCS socket: `/var/run/dsf/dcs.sock`

Whether third-party root plugins are allowed is ultimately gated by DCS configuration through `RootPluginSupport`.

## Build And Verify

```sh
dotnet build DuetPluginService.csproj
dotnet run --project ../PluginManager/PluginManager.csproj -- --help
```

Meaningful end-to-end verification requires DCS plus a plugin directory or ZIP to install.

## Related Docs

- [../../docs/devel/PLUGINS.md](../../docs/devel/PLUGINS.md)
- [../../docs/devel/IPC_PROTOCOL.md](../../docs/devel/IPC_PROTOCOL.md)
- [../PluginManager/README.md](../PluginManager/README.md)
