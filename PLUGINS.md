# DSF Plugins

Starting from version 3.3, DSF will allow the usage of third-party plugins.
They may contain files for execution on a SBC, in DWC, and/or configuration files for RepRapFirmware.

## ZIP File Structure

Every plugin ZIP file may consist of the following files and directories:

- `plugin.json` (required): This file holds general information about the plugin. See the following section for further details
- `dsf/` directory: Contains executable and config files for the SBC portion (if applicable)
- `dwc/` directory: Provides web files that are supposed to be accessible from Duet Web Control. It is symlinked or installed to 0:/www/\<PluginName\>
- `sd/` directory: Holds files that are supposed to be installed into `0:/` (the [virtual] SD card)

For security reasons a plugin bundle must not contain the following files:

- Filenames containing `..`
- `sd/firmware/*`
- `sd/sys/config.g`
- `sd/sys/config-override.g`

## Plugin Manifest

Every plugin must provide a `plugin.json` in the root of its ZIP bundle. It may look like this:

```
{
  "name": "Plugin Name",
  "author": "Plugin Author",
  "version": "1.0.0",
  "license": "Plugin License",
  "homepage": "Plugin Repository",
  "dwcVersion": null,
  "dwcDependencies": [],
  "dwcResources": [],
  "sbcRequired": true,
  "sbcDsfVersion": "3.3",
  "sbcData": {},
  "sbcExecutable": "Plugin Executable"
  "sbcExecutableArguments": null,
  "sbcOutputRedirected":  true,
  "sbcPermissions": [],
  "sbcDependencies": [],
  "rrfVersion": null
}
```

The name may consist of only alphanumeric characters, spaces, dashes (`-`), and underscores (`_`). It must not be empty and may not exceed 64 characters.

## Permissions

Starting from DSF 3.3, third-party plugins may be installed from the web interface.
In order to maintain security, a user installing new plugins is asked for the required permissions before
the plugin may be installed. This must be considered when designing a new external plugin.

This limitation does not effect external plugins running on the Pi (i.e. applications running outside
the `/opt/dsf/plugins` directory as defined per `PluginsDirectory` in `config.json`). These programs
are automatically granted full permissions in DCS to retain backwards-compatibility.

### List of Permissions

DSF 3.2 introduces the following set of permissions:

| Permission name | Description |
| --------------- | ----------- |
| CommandExecution | Execute generic commands |
| CodeInterceptionRead | Read codes from the G/M/T-code streams but do not modify them |
| CodeInterceptionReadWrite | Read codes from the G/M/T-code streams and optionally modify them |
| ManagePlugins | Install, load, unload, and uninstall plugins. Grants FS access to all third-party plugins too |
| ManageUserSessions | Add or remove user sessions |
| ObjectModelRead | Read from the object model |
| ObjectModelReadWrite | Read from and write to the object model |
| RegisterHttpEndpoints | Register HTTP endpoints via Duet Web Server |
| ReadFilaments | Read from `0:/filaments` |
| WriteFilaments | Write to `0:/filaments` |
| ReadFirmware | Read from `0:/firmware` |
| WriteFirmware | Write to `0:/firmware` |
| ReadGCodes | Read from `0:/gcodes` |
| WriteGCodes | Write to `0:/gcodes` |
| ReadMacros | Read from `0:/macros` |
| WriteMacros | Write to `0:/macros` |
| ReadSystem | Read from `0:/sys` |
| WriteSystem | Write to `0:/sys` |
| ReadWeb | Read from `0:/www` |
| WriteWeb | Write to `0:/www` |
| FileSystemAccess | Read and write to all files |
| LaunchProcesses | Launch new processes |
| NetworkAccess | Stand-alone network access |
| SuperUser | Plugin runs as super-user (potentially dangerous) |

### Required Permissions for each Command

At least one of the following permissions must be defined in the plugin manifest in order to use the
corresponding command in DCS:

| Command name | Description | Required permissions |
| ------------ | ----------- | -------------------- |
| GetFileInfo | Parse G-Code file information | CommandExecution, FileSystemAccess, ReadGCodes |
| ResolvePath | Resolve a virtual SD card path to a physical path | CommandExecution, FileSystemAccess |
| Code | Execute an arbitrary G/M/T-code or interpret a comment | CommandExecution |
| EvaluateExpression | Evaluate a meta command expression | CommandExecution |
| Flush | Flush pending G/M/T-codes on a given channel | CommandExecution |
| SimpleCode | Execute a text-based G/M/T-code or interpret a comment | CommandExecution |
| WriteMessage | Write a generic message to the user and/or log file | CommandExecution, ObjectModelReadWrite |
| AddHttpEndpoint | Register a new HTTP endpoint | RegisterHttpEndpoints  |
| RemoveHttpEndpoint | Unregister an existing HTTP endpoint | RegisterHttpEndpoints |
| GetObjectModel | Query the full object model | ObjectModelRead, ObjectModelReadWrite |
| LockObjectModel | Lock the object model for write access | ObjectModelReadWrite |
| SetObjectModel | Set a single key in the object model (e.g. in Scanner) | ObjectModelReadWrite |
| SyncObjectModel | Wait for the object model to be fully synchronized | CommandExecution, ObjectModelRead, ObjectModelReadWrite |
| UnlockObjectModel | Unlock the object model again, see LockObjectModel | ObjectModelReadWrite |
| InstallPlugin | Install or upgrade a plugin | ManagePlugins |
| StartPlugin | Start a plugin | ManagePlugins |
| StartPlugins | Start all the plugins | ManagePlugins |
| SetPluginData | Set custom plugin data in the object model | ObjectModelReadWrite, ManagePlugins* |
| StopPlugin | Stop a plugin (possibly including the one performing this command) | ManagePlugins |
| StopPlugins | Stop all the plugins (possibly including the one performing this command) | ManagePlugins |
| UninstallPlugin | Uninstall a plugin | ManagePlugins |

\* Only needed to modify other data of other plugins

### Shared Data

Every plugin is listed in the DSF object model in the `plugins` namespace.
In this enumeration, plugins can provide shared information for other plugins or for
interfacing parts running in the web interface. It may be modified using the HTTP

`PATCH /machine/pluginData?key=<KEY>`

request but only if the plugin does not provide an executable for the SBC.
If an executable wants to change plugin data, the usage of the `SetPluginData` is advised.
In case the web interface portion needs to change something, create a new HTTP endpoint.

## Lifecycle

Whenever DCS is started, plugins that were started will be restarted. When DCS is shut down, it closes, and
if that fails, forcefully kills, plugin processes.
