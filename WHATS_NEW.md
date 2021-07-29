Summary of important changes in recent versions
===============================================

Version 3.4-b2
==============

New features:
- Plugin manager can now be used to reload specific plugin manifests
- Plugin manager may be used to query the installation/runtime status of plugins
- Added new `cancelling` item to `state.status` enum
- Subscribers and code stream clients can define what messages they want to receive
- Added experimental support for array expressions

Changed behaviour:
- Web server only sends messages with HTTP target to web clients

Bug fixes:
- When RRF reset, DCS could terminate when trying to clear the global variables
- Expression parameters were incorrectly serialized
- Certain UTF-8 strings could be truncated when sent to RRF
- Fixed race condition when attempting to save updated filament mapping
- `GetFileInfo` requests attempted to parse non-GCode files

Version 3.4-b1
==============

New features:
- RRF v3.4-b1 and newer can now read and write files on the SBC

Changed behaviour:
- Replies from macro files are output immediately as they are generated (like in standalone mode)
- Codes starting macros do not collect the results from the sub-macro codes any more (like in standalone mode)
- SPI link timing is not controlled by DCS any more, instead RRF does this (see also M576)

Bug fixes:
- Variable names could not contain underscores

Version 3.3.0
=============

Upgrade notes since version 3.2:
- Plugin support is now enabled but requires some extra steps before they can be installed. Execute the following commands in a **Linux** terminal:
  1. Install AppArmor package: `sudo apt install apparmor`
  2. Enable AppArmor support on boot: `echo "  lsm=apparmor" >> /boot/cmdline.txt`
  3. Enable new plugin services: `sudo systemctl enable duetpluginservice && sudo systemctl enable duetpluginservice-root`
  4. Reboot the SBC to apply the changes: `sudo reboot`
- [DuetPiManagementPlugin](https://github.com/Duet3D/DuetSoftwareFramework/tree/v3.3-dev/src/DuetPiManagementPlugin) is now available for DuetPi to support various M-codes that werre previously supported only in standalone mode
  1. This plugin is available from the `duetpimanagementplugin` package which comes with the latest DuetPi version
  2. It can be manually installed via `apt install duetpimanagementplugin`
  3. Go to the `Machine-Specific -> Third-Party Plugins` page on DWC to enable it after a manual installation
- There is now a new configuration file `dsf-config.g` which is executed when all previously started plugins have been started by the plugin service(s) after a reboot. Put custom G/M-codes into this file, ideally with a short delay at the start (`G4 S2`) to ensure the plugions are fully started at the time custom actions are executed.

Bug fixes:
- When a new firmware build was installed, the final IAP delay was sometimes too short
- `directories` key was not queried when DCS started in update-only mode
- Remaining plugins were not correctly stopped when a plugin service was shut down

Known limitations:
- Auto-resume on power loss cannot be configured (M916)
- Accelerometer profiles cannot be recorded (M956)
- Password protection has no effect yet (M551)

Version 3.3-rc3
===============

Compatible versions:
- RepRapFirmware 3.3-rc3
- DuetWebControl 3.3-rc3

Bug fixes:
- M0 called `stop.g` instead of `cancel.g` when a print was cancelled
- DCS does no longer check SPI responses from IAP while upgrading the firmware to lower the risk of upgrade issues
- Plugins were never started in non-SPI mode
- DCS postinst script failed if the service was previously enabled but no Duet was attached at the time of the upgrade
- When codes were intercepted, the `SourceConnection` property was not always populated
- When DCS was configured to never terminate on reset, sometimes it failed to establish a connection again
- Subscribe connection clients failed to clear `global` dictionary on reset
- Fixed `exists` function in conditional G-code
- When retrieving file info, DCS returned the simulated time instead of the slicer time
- Fixed timing issue causing simulation times to be written incorrectly to G-code files

Version 3.3-rc2
===============

Compatible versions:
- RepRapFirmware 3.3-rc2
- DuetWebControl 3.3-rc2

New features:
- Added new `Disconnected` state to indicate when DSF/DWC is not connected to RRF
- .deb packages can be installed over DWC if support for package management and root plugins is enabled (see plugins.json and config.json)

Changed behaviour:
- Only G0 and G1 are repeated for Fanuc-style G-code to follow RRF implementation
- M955 P-parameter is passed as a driver identifier like M569 P
- Added extra fallbacks for main firmware files (`directories.firmware` -> `0:/firmware` -> `0:/sys`)

Version 3.3-rc1
===============

Compatible versions:
- RepRapFirmware 3.3-rc1
- DuetWebControl 3.3-rc1

New features:
- Added support for abbreviated G-codes in CNC and Laser mode (Fanuc and LaserWeb G-code style)
- Added new code streaming mode to the API for easier Repetier and Octoprint integration
- Implemented new command line utility to test and demonstrate the new code stream mode

Bug fixes:
- Global variables in the OM were not cleared when DSF lost connection to RRF
- When prints were paused during macros, under special cirumstances the macro was run again on resume

Version 3.3-b3
==============

Compatible versions:
- RepRapFirmware 3.3-b3
- DuetWebControl 3.3-b3

New features:
- Added support for variables in G-code files (`global`, `var`, `set`)

Changed behaviour:
- Added fallback to `directories.system` if firmware files cannot be found in `directories.firmware`

Bug fixes:
- Tilt factors for delta kinematics were off in config-override.g
- M0/M1 didn't cancel already queued file codes
- Truncated tool lists are now fetched using multiple queries

Version 3.3-b2
==============

Compatible versions:
- RepRapFirmware 3.3-b2
- DuetWebControl 3.3-b2

Changed behaviour:
- Changed file format of the new plugin manifest
- Layers code is no longer compatible with previous versions
- Emulation is always applied to results from code interceptors
- The DSF-only keyword `return` is now obsolete and will be removed from a future version (unless support for it is added to RRF)

Bug fixes:
- Updated regular expressions for various slicer formats
- SBC permissions for third-party plugins were not properly enforced
- When DCS failed to start too many times in a row, it remained disabled
- DPS didn't (de-)serialize string dictionaries properly

Known issues:
- Variable support isn't implemented yet, this is TBD in 3.3-b3

Version 3.3-b1
==============

Compatible versions:
- RepRapFirmware 3.3-b1
- DuetWebControl 3.3-b1

Changed behaviour:
- Plugin support in DSF is fully implemented
- DuetWebServer notifies systemd when it has started like DuetControlServer does
- `M999` is only prioritized from text inputs if it is intended for the main board
- New configuration file `dsf-config.g` is automatically executed when DCS and third-party plugins have been started
- CodeConsole application uses Telnet G-code channel instead of SBC

Known issues:
- `network.interfaces[].speed` does not work for WiFi adapters on the Pi (see https://github.com/dotnet/runtime/issues/47777)

Version 3.2.2
=============

Compatible versions:
- RepRapFirmware 3.2.2
- DuetWebControl 3.2.2

Bug fixes:
- Tabs at the beginning of G-code lines were not interpreted as up to 4 spaces
- Payloads for conditional keywords had to be encapsulated in curly braces to allow usage of round braces
- Parameters for codes that expected unprecedented parameters always had to be encapsulated in double quotes
- When DWS was configured for a different port, no WebSocket sessions were permitted without extra CORS exception
- DCS could be killed by systemd if runonce.g didn't finish quickly enough
- `break` and `continue` didn't wait for pending codes to finish which could lead to problems with `iterations`
- When empty comments were parsed, the `Comment` field of DSF codes remained `null` instead of `string.Empty`
- Unlike in RRF `G29 S0` accepted custom filenames (better solution is to use `G29` followed by `G29 S3`)

Version 3.2.0
=============

Compatible versions:
- RepRapFirmware 3.2.0
- DuetWebControl 3.2.0

Bug fixes:
- M929 didn't set the correct log level
- `abort` tried to evaluate following expression even if it was not specified
- `abort` did not always cancel all the internal codes in time
- Under rare conditions suspended codes could be re-suspended in the wrong order
- G0/G1 with dynamic feedrate expressions caused an internal exception

Version 3.2.0-rc2
================

Compatible files:
- RepRapFirmware 3.2.0-rc2
- DuetWebControl 3.2.0-rc2

Changed behaviour:
- Increased SPI connection timeout from 2.5s to 4s (same value as in RRF)
- Partial SPI transmissions may not take longer than 500ms (same value as in RRF)

Bug fixes:
- M997 and M999 could generate "Operation has been cancelled" errors
- When DCS terminated sockets of command connections were not correctly shut down
- Object model write locks were not correctly disposed of when DCS terminated
- Calling abort in macro files could cause an exception
- M501 could freeze if no config-override.g was found
- "Macro not found" warning messages were not output as part of code results

Version 3.2.0-rc1
================

Compatible files:
- RepRapFirmware 3.2.0-rc1
- DuetWebControl 3.2.0-rc1

Changed behaviour:
- RRF downgrades from later protocol versions are now possible
- CORS headers are only sent if explictly configured by `M586 C`-parameter
- SPI transfers use CRC32 instead of CRC16 with new protocol version
- DCS service notifies systemd when it is up and running
- DCS terminates when a firmware update of the main board is complete (unless `NoTerminateOnReset` is set)
- `runonce.g` is no longer processed if DCS starts in update-only mode (i.e. with `-u` parameter)
- Third-party DSF plugins cannot be installed any more (TBD for v3.3)

Bug fixes:
- Fixed incompatibilities when updating RRF from older firmware versions
- LockMovementAndWaitForStandstill retransmissions were logged
- Expressions were not automatically evaluated in the code processors
- Internally processed codes were only logged if they resulted in a warning or error
- DWS didn't send correct `Cache-Control` header which could result in DuetPi using outdated DWC versions
- Sometimes the filament mapping was not fully restored if the `NoTerminateOnReset` option was enabled
- M929 was not fully implemented for new log levels

Version 3.2.0-b4
================

Compatible files:
- RepRapFirmware 3.2.0-b4
- DuetWebControl 3.2.0-b4

Changed behaviour:
- DCS service is now started via sysinit.target instead of basic.target so that config.g is processed faster on boot
- Thumbnails from PrusaSlicer are now parsed (thanks Sindarius)
- M500 writes new heater tuning parameters to config-override.g
- In Marlin emulation "ok" responses are only sent when the line is complete

Bug fixes:
- Codes could be sent to code interceptors in the wrong order
- M21 (P0) returned an error message breaking Octoprint support
- Under certain circumstances some object model keys were not updated on initialisation
- DCS service didn't have permission to change the datetime
- Print times with decimal places were incorrectly parsed
- When the controller was reset or updated, an extra data transfer was performed

Version 3.2.0-b3
================

Compatible files:
- RepRapFirmware 3.2.0-b3
- DuetWebControl 3.2.0-b3

Changed behaviour:
- DCS is now explicitly notified about closed messages and files (hence no longer compatible with 3.2-b2)
- CodeConsole utility allows evaluation of expressions using `eval <expression>`

Bug fixes:
- When certain G-code inputs were disabled, the DSF API threw NullReferenceExceptions
- When the heaters contained null items, no config-override.g could be writen
- When the move compensation type was set to none, the heightmap file was not reset
- Starting macro files could cause out-of-order execution and stack underruns
- Sometimes the object model wasn't fully updated after a disconnect

Version 3.2.0-b2
================

Compatible files:
- RepRapFirmware 3.2.0-b2
- DuetWebControl 3.2.0-b2

Changed behaviour:
- runonce.g is no longer processed before config.g to match RRF's behaviour
- Increased SPI protocol version due to slight changes for the new Linux task in RRF

Bug fixes:
- Added missing "Starting" item to the MachineStatus enumeration
- M112/M999 were executed out-of-order when read from files
- Sometimes in print files codes invoking macro files could crash DCS
- Aborted macro files did not cancel codes properly
- Comments following codes directly without a whitespace could cause parsing errors

Version 3.2.0-b1
================

Compatible files:
- RepRapFirmware 3.2.0-b1
- DuetWebControl 3.2.0-b1

Changed behaviour:
- Permissions are now required for DSF commands for executables running in `/opt/dsf/plugins`
- Simulation times are automatically written to G-code files
- Increased API level due to object model changes
- Renamed namespace `DuetAPI.Machine` to `DuetAPI.ObjectModel` and `MachineModel` to `ObjectModel`
- DSF processes are now running as their own `dsf` user

Bug fixes:
- DCS service file contained an invalid CPU priority
- Event logging via M929 was not working
- Macro files with an unknown start code were not properly cancelled by M99/M292 P1

Known issues:
- Security permissions are not enforced via AppArmor yet (and they are still subject to change)
- Package dependencies are not yet installed when a plugin is installed

Version 3.1.1
==============

Compatible files:
- RepRapFirmware 3.1.1
- DuetWebControl 3.1.1

Changed behaviour:
- Increased API level due to new object model fields
- Code replies from the firmware are now trimmed at the end right after receipt

Bug fixes:
- Final replies from system macros were discarded
- Substituted macro filenames were incorrect in the DCS log
- Codes requesting message boxes could be executed twice
- Message boxes could be closed internally in DCS when not supposed to
- Codes from pause.g were cancelled under certain circumstances

Version 3.1.0
==============

Compatible files:
- RepRapFirmware 3.1.0
- DuetWebControl 3.1.0

Changed behaviour:
- Duplicate code parameters are now ignored
- If M122 cannot obtain locks in DCS within 2s, the lock is ignored
- SPI poll delay is skipped during updates

Bug fixes:
- When pausing a print at the end of a file, the file was closed on resume
- M500 P10 did not work
- DCS parameter for updates (-u) was broken if another instance was not started
- Whole-line comments were not truncated before they were sent to RRF
- Only the first filament usage was parsed from files generated by Prusa Slicer
- M505 did not return the current sys directory when invoked without parameters
- Whole-line comments preceding a code that requests a macro file could cause the code to be executed twice

Version 2.2.0
==============

Compatible files:
- RepRapFirmware 3.01-RC12
- DuetWebControl 2.1.7

Changed behaviour:
- Changed letter of unprecedented parameters from '\0' to '@'
- Increased default and minimum API version number to 7
- Whole line comments are now sent to RepRapFirmware

Known issues:
- Print/Simulation times are not written to G-code files
- Codes with invalid expressions may not instantly terminate a macro or job file

Bug fixes:
- Expressions in square brackets were not evaluatated
- M500 wrote workplace coordinates without offsetting the indices by 1

Version 2.1.3
==============

Compatible files:
- RepRapFirmware 3.01-RC11
- DuetWebControl 2.1.6

Changed behaviour:
- Warning message is shown in the DCS log when API clients with an old version number connect

Known issues:
- Print/Simulation times are not written to G-code files
- Comments for object cancellation detection are not parsed (work-around is to use M486 directly)
- Codes with invalid expressions may not instantly terminate a macro or job file

Bug fixes:
- Unchanged arrays could be reported in Patch subscription mode
- Initial query in Patch mode was not working
- Web server did not clear HTTP endpoints under certain circumstances
- echo expressions were not parsed correctly if strings contained commas
- Changing the system time just before a controller reset could lead to an abnormal program termination

Version 2.1.2
==============

Compatible files:
- RepRapFirmware 3.01-RC10
- DuetWebControl 2.1.5

Known issues:
- Print/Simulation times are not written to G-code files
- Comments for object cancellation detection are not parsed (work-around is to use M486 directly)
- Codes with invalid expressions may not instantly terminate a macro or job file

Bug fixes:
- Leading G53 wasn't added to string representations of parsed codes
- Starting DCS with the fifo CPU scheduler via systemd could lead to maximum CPU usage
- Some nullable RRF OM fields were not declared as such in the DSF OM

Version 2.1.1
==============

Compatible files:
- RepRapFirmware 3.01-RC10
- DuetWebControl 2.1.5

Changed behaviour:
- If DCS cannot establish a connection to RRF, the error message is always printed
- Code parser exceptions report the filename
- File info parser scans parsed comments in the file footer like in the file header
- Increased priority in systemd service for DCS and start it at `basic.target` instead of `multi-user.target`

Known issues:
- Print/Simulation times are not written to G-code files
- Comments for object cancellation detection are not parsed (work-around is to use M486 directly)
- Codes with invalid expressions may not instantly terminate a macro or job file

Bug fixes:
- Expression code parameters were not properly printed in the log
- Double quotes were incorrectly parsed
- limits key was not updated in the object model
- Height map file was overwritten by the RepRapFirmware object model
- G29 S1/M375 didn't print an offset warning when a heightmap was loaded without homing Z first
- Order of M0/M1 and notification about the print being cancelled was wrong
- Some internal fields of the Code object were incorrectly serialized
- Codes could finish in the wrong order
- PrusaSlicer print time and layer height were not parsed correctly
- Expression fields were always evaluated from the DSF object model

Version 2.1.0
==============

Compatible files:
- RepRapFirmware 3.01-RC9
- DuetWebControl 2.1.4

Changed behaviour:
- Implemented conditional G-code according to https://duet3d.dozuki.com/Wiki/GCode_Meta_Commands (same command set as supported by RRF)
- DuetAPI version number has been increased, however the previous one is still accepted
- DuetAPI uses relaxed JSON escaping like in the DCS settings file
- Added new fields stepsPerMm and microstepping to Axis amd Extruder items to DuetAPI
- Increased maximum size of messages being sent to the firmware from 256 bytes to 4KiB
- Removed SpiPollDelaySimulating and renamed SpiPollDelaySimulating to FileBufferSize in the DCS settings (the latter is now used for code files, too)
- Simple text-based codes no longer report when they are cancelled

Known issues:
- Print/Simulation times are not written to G-code files
- Comments for object cancellation detection are not parsed (work-around is to use M486 directly)

Bug fixes:
- DuetControlServer could sporadically hang when printing a file
- Fixed deadlock that could occur when the SPI task tried to resolve pending requests
- M20 was not fully compatible with RRF
- Concatenating code parser exception caused the line to be appended multiple times
- Filament sensors and move.kinematics were neither properly updated nor serialized
- Codes of macros being cancelled were sometimes aborted with a wrong exception

Version 2.0.0
==============
Compatible files:
- RepRapFirmware 3.01-RC8
- DuetWebControl 2.1.3

Changed behaviour:
- M999 stops DCS. This behaviour can be changed by starting it with the `-r` command-line argument or by changing the config value `NoTerminateOnReset` to `true`
- Plugins using prior API versions are no longer compatible and require new versions of the API libraries
- Codes M21+M22 are not supported and will throw an error
- Code expressions are now preparsed and Linux object model fields are substituted before the final evaluation

Known issues:
- Conditional G-codes (aka meta commands) except for echo are not supported yet
- Print/Simulation times are not written to G-code files
- Comments for object cancellation detection are not parsed (work-around is to use M486 directly)

Bug fixes:
- Added compatibility for G-code meta expressions
- When all macros were aborted the messages were not properly propagated to the start code(s)
- Some codes were incorrectly sent when aborting all files
- Some macro codes could be executed in the wrong order when multiple macros were invoked
- Code requests from the firmware could cause a deadlock
