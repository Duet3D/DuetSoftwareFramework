Summary of important changes in recent versions
===============================================

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

- Rotating magnet filament sensor was not properly instantiated in the OM

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
