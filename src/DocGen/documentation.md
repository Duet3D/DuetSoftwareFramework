# Overview

**This page is auto-generated. If you find any errors, please report them on the [forum](https://forum.duet3d.com) and DO NOT EDIT this page**

All Duet software projects share the same object model to store configuration and sensor data.
This page provides documentation about the different object model keys and associated properties.

Some fields may not be available in standalone mode because some fields are only maintained by DSF and/or DWC.
It is advised to consider this when developing applications that address Duets in standalone *and* SBC mode.

*This page is preliminary and is subject to further changes.*

# Object Model Description

## boards[]
List of connected boards

*Note:* The first item represents the main board

#### boards[].bootloaderFileName
Filename of the firmware binary

#### boards[].canAddress
CAN address of this board or null if not applicable

#### boards[].directDisplay
Details about a connected display or null if none is connected

##### boards[].directDisplay.pulsesPerClick
Number of pulses per click of the rotary encoder

##### boards[].directDisplay.spiFreq
SPI frequency of the display (in Hz)

##### boards[].directDisplay.typeName
Name of the attached display type

#### boards[].firmwareDate
Date of the firmware build

#### boards[].firmwareFileName
Filename of the firmware binary

#### boards[].firmwareName
Name of the firmware build

#### boards[].firmwareVersion
Version of the firmware build

#### boards[].iapFileNameSBC
Filename of the IAP binary that is used for updates from the SBC or null if unsupported

#### boards[].iapFileNameSD
Filename of the IAP binary that is used for updates from the SD card or null if unsupported

#### boards[].maxHeaters
Maximum number of heaters this board can control

#### boards[].maxMotors
Maximum number of motors this board can drive

#### boards[].mcuTemp
Minimum, maximum, and current temperatures of the MCU

#### boards[].name
Full name of the board

#### boards[].shortName
Short name of this board

#### boards[].state
State of this board

#### boards[].supports12864
Indicates if this board supports external 12864 displays

#### boards[].supportsDirectDisplay
Indicates if this board supports external displays

#### boards[].uniqueId
Unique identifier of the board

#### boards[].v12
Minimum, maximum, and current voltages on the 12V rail

#### boards[].vIn
Minimum, maximum, and current voltages on the input rail

## directories
Information about the individual directories

*Note:* This may not be available in RepRapFirmware if no mass storages are available

#### directories.filaments
Path to the filaments directory

#### directories.firmware
Path to the firmware directory

#### directories.gCodes
Path to the G-Codes directory

#### directories.macros
Path to the macros directory

#### directories.menu
Path to the menu directory

*Note:* Intended for 12864 displays but currently unused in DSF. It is only needed for the Duet Maestro + DWC

#### directories.scans
Path to the scans directory

#### directories.system
Path to the system directory

#### directories.web
Path to the web directory

## fans[]
List of configured fans

#### fans[].actualValue
Value of this fan (0..1 or -1 if unknown)

#### fans[].blip
Blip value indicating how long the fan is supposed to run at 100% when turning it on to get it started (in s)

#### fans[].frequency
Configured frequency of this fan (in Hz)

#### fans[].max
Maximum value of this fan (0..1)

#### fans[].min
Minimum value of this fan (0..1)

#### fans[].name
Name of the fan

#### fans[].requestedValue
Requested value for this fan on a scale between 0 to 1

#### fans[].rpm
Current RPM of this fan or -1 if unknown/unset

#### fans[].thermostatic
Thermostatic control parameters

##### fans[].thermostatic.heaters[]
List of the heaters to monitor (indices)

##### fans[].thermostatic.highTemperature
Upper temperature range required to turn on the fan (in C)

##### fans[].thermostatic.lowTemperature
Lower temperature range required to turn on the fan (in C)

## global[]
Dictionary of global variables vs JSON values

*Note:* When DSF attempts to reconnect to RRF, this may be set to null to clear the contents

## heat
Information about the heat subsystem

#### heat.bedHeaters[]
List of configured bed heaters (indices)

*Note:* Items may be -1 if unconfigured

#### heat.chamberHeaters[]
List of configured chamber heaters (indices)

*Note:* Items may be -1 if unconfigured

#### heat.coldExtrudeTemperature
Minimum required temperature for extrusion moves (in C)

#### heat.coldRetractTemperature
Minimum required temperature for retraction moves (in C)

#### heat.heaters[]
List of configured heaters

##### heat.heaters[].active
Active temperature of the heater (in C)

##### heat.heaters[].current
Current temperature of the heater (in C)

##### heat.heaters[].max
Maximum temperature allowed for this heater (in C)

*Note:* This is only temporary and should be replaced by a representation of the heater protection as in RRF

##### heat.heaters[].min
Minimum temperature allowed for this heater (in C)

*Note:* This is only temporary and should be replaced by a representation of the heater protection as in RRF

##### heat.heaters[].model
Information about the heater model

##### heat.heaters[].model.deadTime
Dead time

##### heat.heaters[].model.enabled
Indicates if this heater is enabled

##### heat.heaters[].model.gain
Gain value

##### heat.heaters[].model.heatingRate
Heating rate (in K/s)

##### heat.heaters[].model.inverted
Indicates if the heater PWM signal is inverted

##### heat.heaters[].model.maxPwm
Maximum PWM value

##### heat.heaters[].model.pid
Details about the PID controller

##### heat.heaters[].model.pid.overridden
Indicates if custom PID values are used

##### heat.heaters[].model.pid.p
Proportional value of the PID regulator

##### heat.heaters[].model.pid.i
Integral value of the PID regulator

##### heat.heaters[].model.pid.d
Derivative value of the PID regulator

##### heat.heaters[].model.pid.used
Indicates if PID control is being used

##### heat.heaters[].model.standardVoltage
Standard voltage or null if unknown

##### heat.heaters[].model.timeConstant
Time constant

##### heat.heaters[].model.timeConstantFansOn
Time constant with the fans on

##### heat.heaters[].monitors[]
Monitors of this heater

##### heat.heaters[].monitors[].action
Action to perform when the trigger condition is met

##### heat.heaters[].monitors[].condition
Condition to meet to perform an action

##### heat.heaters[].monitors[].limit
Limit threshold for this heater monitor

##### heat.heaters[].name
Name of the heater or null if unset

##### heat.heaters[].sensor
Sensor number of this heater or -1 if not configured

##### heat.heaters[].standby
Standby temperature of the heater (in C)

##### heat.heaters[].state
State of the heater

## httpEndpoints[]
List of registered third-party HTTP endpoints

#### httpEndpoints[].endpointType
HTTP type of this endpoint

#### httpEndpoints[].namespace
Namespace of the endpoint

*Note:* May be  to register root-level rr_ requests (to emulate RRF poll requests)

#### httpEndpoints[].path
Path to the endpoint

#### httpEndpoints[].isUploadRequest
Whether this is a upload request

*Note:* If set to true, the whole body payload is written to a temporary file and the file path is passed via the  property

#### httpEndpoints[].unixSocket
Path to the UNIX socket

## inputs[]
Information about every available G/M/T-code channel

## job
Information about the current job

#### job.build
Information about the current build or null if not available

##### job.build.currentObject
Index of the current object being printed or -1 if unknown

##### job.build.m486Names
Whether M486 names are being used

##### job.build.m486Numbers
Whether M486 numbers are being used

##### job.build.objects[]
List of detected build objects

##### job.build.objects[].cancelled
Indicates if this build object is cancelled

##### job.build.objects[].name
Name of the build object (if any)

##### job.build.objects[].x[]
X coordinates of the build object (in mm or null if not found)

##### job.build.objects[].y[]
Y coordinates of the build object (in mm or null if not found)

#### job.duration
Total duration of the current job (in s or null)

#### job.file
Information about the file being processed

##### job.file.filament[]
Filament consumption per extruder drive (in mm)

##### job.file.fileName
The filename of the G-code file

##### job.file.firstLayerHeight
Height of the first layer or 0 if not found (in mm)

##### job.file.generatedBy
Name of the application that generated this file

##### job.file.height
Build height of the G-code job or 0 if not found (in mm)

##### job.file.lastModified
Value indicating when the file was last modified or null if unknown

##### job.file.layerHeight
Height of each other layer or 0 if not found (in mm)

##### job.file.numLayers
Number of total layers or 0 if unknown

##### job.file.printTime
Estimated print time (in s)

##### job.file.simulatedTime
Estimated print time from G-code simulation (in s)

##### job.file.size
Size of the file

##### job.file.thumbnails[]
Collection of thumbnails parsed from Gcode

##### job.file.thumbnails[].encodedImage
base64 encoded thumbnail

##### job.file.thumbnails[].height
Height of thumbnail

##### job.file.thumbnails[].width
Width of thumbnail

#### job.filePosition
Current position in the file being processed (in bytes or null)

#### job.lastDuration
Total duration of the last job (in s or null)

#### job.lastFileName
Name of the last file processed or null if none

#### job.lastFileAborted
*This field is maintained by DSF in SBC mode and might not be available in standalone mode*

Indicates if the last file was aborted (unexpected cancellation)

#### job.lastFileCancelled
*This field is maintained by DSF in SBC mode and might not be available in standalone mode*

Indicates if the last file was cancelled (user cancelled)

#### job.lastFileSimulated
*This field is maintained by DSF in SBC mode and might not be available in standalone mode*

Indicates if the last file processed was simulated

*Note:* This is not set if the file was aborted or cancelled

#### job.layer
Number of the current layer or null not available

#### job.layers[]
*This field is maintained by DSF in SBC mode and might not be available in standalone mode*

Information about the past layers

*Note:* In previous API versions this was a  but it has been changed to  to
allow past layers to be modified again when needed. Note that previous plugins subscribing to this property will not receive any more
updates about this property to avoid memory leaks

##### job.layers[].duration
Duration of the layer (in s)

##### job.layers[].filament[]
Actual amount of filament extruded during this layer (in mm)

##### job.layers[].fractionPrinted
Fraction of the file printed during this layer (0..1)

##### job.layers[].height
Height of the layer (in mm or 0 if unknown)

##### job.layers[].temperatures[]
Last heater temperatures (in C or null if unknown)

#### job.layerTime
Time elapsed since the last layer change (in s or null)

#### job.pauseDuration
Total pause time since the job started

#### job.rawExtrusion
Total extrusion amount without extrusion factors applied (in mm)

#### job.timesLeft
Estimated times left

##### job.timesLeft.filament
Time left based on filament consumption (in s or null)

##### job.timesLeft.file
Time left based on file progress (in s or null)

##### job.timesLeft.slicer
Time left based on the slicer reports (see M73, in s or null)

#### job.warmUpDuration
Time needed to heat up the heaters (in s or null)

## limits
Machine configuration limits

#### limits.axes
Maximum number of axes or null if unknown

#### limits.axesPlusExtruders
Maximum number of axes + extruders or null if unknown

#### limits.bedHeaters
Maximum number of bed heaters or null if unknown

#### limits.boards
Maximum number of boards or null if unknown

#### limits.chamberHeaters
Maximum number of chamber heaters or null if unknown

#### limits.drivers
Maximum number of drivers or null if unknown

#### limits.driversPerAxis
Maximum number of drivers per axis or null if unknown

#### limits.extruders
Maximum number of extruders or null if unknown

#### limits.extrudersPerTool
Maximum number of extruders per tool or null if unknown

#### limits.fans
Maximum number of fans or null if unknown

#### limits.gpInPorts
Maximum number of general-purpose input ports or null if unknown

#### limits.gpOutPorts
Maximum number of general-purpose output ports or null if unknown

#### limits.heaters
Maximum number of heaters or null if unknown

#### limits.heatersPerTool
Maximum number of heaters per tool or null if unknown

#### limits.monitorsPerHeater
Maximum number of monitors per heater or null if unknown

#### limits.restorePoints
Maximum number of restore points or null if unknown

#### limits.sensors
Maximum number of sensors or null if unknown

#### limits.spindles
Maximum number of spindles or null if unknown

#### limits.tools
Maximum number of tools or null if unknown

#### limits.trackedObjects
Maximum number of tracked objects or null if unknown

#### limits.triggers
Maximum number of triggers or null if unknown

#### limits.volumes
Maximum number of volumes or null if unknown

#### limits.workplaces
Maximum number of workplaces or null if unknown

#### limits.zProbeProgramBytes
Maximum number of Z-probe programming bytes or null if unknown

#### limits.zProbes
Maximum number of Z-probes or null if unknown

## messages[]
*This field is maintained by DSF in SBC mode and might not be available in standalone mode*

Generic messages that do not belong explicitly to codes being executed.
This includes status messages, generic errors and outputs generated by M118

## move
Information about the move subsystem

#### move.axes[]
List of the configured axes

##### move.axes[].acceleration
Acceleration of this axis (in mm/s^2)

##### move.axes[].babystep
Babystep amount (in mm)

##### move.axes[].current
Motor current (in mA)

##### move.axes[].drivers[]
List of the assigned drivers

##### move.axes[].homed
Whether or not the axis is homed

##### move.axes[].jerk
Motor jerk (in mm/s)

##### move.axes[].letter
Letter of the axis (always upper-case)

##### move.axes[].machinePosition
Current machine position (in mm) or null if unknown/unset

##### move.axes[].max
Maximum travel of this axis (in mm)

##### move.axes[].maxProbed
Whether the axis maximum was probed

##### move.axes[].microstepping
Microstepping configuration

##### move.axes[].microstepping.interpolated
Indicates if the stepper driver uses interpolation

##### move.axes[].microstepping.value
Microsteps per full step

##### move.axes[].min
Minimum travel of this axis (in mm)

##### move.axes[].minProbed
Whether the axis minimum was probed

##### move.axes[].speed
Maximum speed (in mm/s)

##### move.axes[].stepsPerMm
Number of microsteps per mm

##### move.axes[].userPosition
Current user position (in mm) or null if unknown

##### move.axes[].visible
Whether or not the axis is visible

##### move.axes[].workplaceOffsets[]
Offsets of this axis for each workplace (in mm)

#### move.calibration
Information about the automatic calibration

##### move.calibration.final
Final calibration results (for Delta calibration)

##### move.calibration.final.deviation
RMS deviation (in mm)

##### move.calibration.final.mean
Mean deviation (in mm)

##### move.calibration.initial
Initial calibration results (for Delta calibration)

##### move.calibration.initial.deviation
RMS deviation (in mm)

##### move.calibration.initial.mean
Mean deviation (in mm)

##### move.calibration.numFactors
Number of factors used (for Delta calibration)

#### move.compensation
Information about the currently configured compensation options

##### move.compensation.fadeHeight
Effective height before the bed compensation is turned off (in mm) or null if not configured

##### move.compensation.file
*This field is maintained by DSF in SBC mode and might not be available in standalone mode*

Full path to the currently used height map file or null if none is in use

##### move.compensation.meshDeviation
Deviations of the mesh grid or null if not applicable

##### move.compensation.meshDeviation.deviation
RMS deviation (in mm)

##### move.compensation.meshDeviation.mean
Mean deviation (in mm)

##### move.compensation.probeGrid
Settings of the current probe grid

##### move.compensation.probeGrid.axes[]
Axis letters of this heightmap

##### move.compensation.probeGrid.maxs[]
End coordinates of the heightmap

##### move.compensation.probeGrid.mins[]
Start coordinates of the heightmap

##### move.compensation.probeGrid.radius
Probing radius for delta kinematics

##### move.compensation.probeGrid.spacings[]
Spacings between the coordinates

##### move.compensation.skew
Information about the configured orthogonal axis parameters

##### move.compensation.skew.compensateXY
Indicates if the  value is applied to the X or Y axis value

##### move.compensation.skew.tanXY
Tangent of the skew angle for the XY or YX axes

##### move.compensation.skew.tanXZ
Tangent of the skew angle for the XZ axes

##### move.compensation.skew.tanYZ
Tangent of the skew angle for the YZ axes

##### move.compensation.type
Type of the compensation in use

#### move.currentMove
Information about the current move

##### move.currentMove.acceleration
Acceleration of the current move (in mm/s^2)

##### move.currentMove.deceleration
Deceleration of the current move (in mm/s^2)

##### move.currentMove.laserPwm
Laser PWM of the current move (0..1) or null if not applicable

##### move.currentMove.requestedSpeed
Requested speed of the current move (in mm/s)

##### move.currentMove.topSpeed
Top speed of the current move (in mm/s)

#### move.extruders[]
List of configured extruders

##### move.extruders[].acceleration
Acceleration of this extruder (in mm/s^2)

##### move.extruders[].current
Motor current (in mA)

##### move.extruders[].driver
Assigned driver

##### move.extruders[].filament
Name of the currently loaded filament

##### move.extruders[].factor
Extrusion factor to use (0..1 or greater)

##### move.extruders[].jerk
Motor jerk (in mm/s)

##### move.extruders[].microstepping
Microstepping configuration

##### move.extruders[].microstepping.interpolated
Indicates if the stepper driver uses interpolation

##### move.extruders[].microstepping.value
Microsteps per full step

##### move.extruders[].nonlinear
Nonlinear extrusion parameters (see M592)

##### move.extruders[].nonlinear.a
A coefficient in the extrusion formula

##### move.extruders[].nonlinear.b
B coefficient in the extrusion formula

##### move.extruders[].nonlinear.upperLimit
Upper limit of the nonlinear extrusion compensation

##### move.extruders[].position
Extruder position (in mm)

##### move.extruders[].pressureAdvance
Pressure advance

##### move.extruders[].rawPosition
Raw extruder position as commanded by the slicer without extrusion factor applied (in mm)

##### move.extruders[].speed
Maximum speed (in mm/s)

##### move.extruders[].stepsPerMm
Number of microsteps per mm

#### move.idle
Idle current reduction parameters

##### move.idle.timeout
Idle timeout after which the stepper motor currents are reduced (in s)

##### move.idle.factor
Motor current reduction factor (0..1)

#### move.kinematics
Configured kinematics options

##### move.kinematics (CoreKinematics)
Information about core kinematics

##### move.kinematics.forwardMatrix[] (CoreKinematics)
Forward matrix

##### move.kinematics.inverseMatrix[] (CoreKinematics)
Inverse matrix

##### move.kinematics.tiltCorrection (CoreKinematics)
Parameters describing the tilt correction

##### move.kinematics.tiltCorrection (CoreKinematics)
Tilt correction parameters for Z leadscrew compensation

##### move.kinematics.tiltCorrection.correctionFactor (CoreKinematics)
Correction factor

##### move.kinematics.tiltCorrection.lastCorrections[] (CoreKinematics)
Last corrections (in mm)

##### move.kinematics.tiltCorrection.maxCorrection (CoreKinematics)
Maximum Z correction (in mm)

##### move.kinematics.tiltCorrection.screwPitch (CoreKinematics)
Pitch of the Z leadscrews (in mm)

##### move.kinematics.tiltCorrection.screwX[] (CoreKinematics)
X positions of the leadscrews (in mm)

##### move.kinematics.tiltCorrection.screwY[] (CoreKinematics)
Y positions of the leadscrews (in mm)

##### move.kinematics.name (CoreKinematics)
Currently configured geometry type

##### move.kinematics (DeltaKinematics)
Delta kinematics

##### move.kinematics.deltaRadius (DeltaKinematics)
Delta radius (in mm)

##### move.kinematics.homedHeight (DeltaKinematics)
Homed height of a delta printer in mm

##### move.kinematics.printRadius (DeltaKinematics)
Print radius for Hangprinter and Delta geometries (in mm)

##### move.kinematics.towers[] (DeltaKinematics)
Delta tower properties

##### move.kinematics.towers[] (DeltaKinematics)
Delta tower properties

##### move.kinematics.towers[].angleCorrection (DeltaKinematics)
Tower position corrections (in degrees)

##### move.kinematics.towers[].diagonal (DeltaKinematics)
Diagonal rod length (in mm)

##### move.kinematics.towers[].endstopAdjustment (DeltaKinematics)
Deviation of the ideal endstop position (in mm)

##### move.kinematics.towers[].xPos (DeltaKinematics)
X coordinate of this tower (in mm)

##### move.kinematics.towers[].yPos (DeltaKinematics)
Y coordinate of this tower (in mm)

##### move.kinematics.xTilt (DeltaKinematics)
How much Z needs to be raised for each unit of movement in the +X direction

##### move.kinematics.yTilt (DeltaKinematics)
How much Z needs to be raised for each unit of movement in the +Y direction

##### move.kinematics.name (DeltaKinematics)
Currently configured geometry type

##### move.kinematics (HangprinterKinematics)
Information about hangprinter kinematics

##### move.kinematics.anchors[] (HangprinterKinematics)
Anchor configurations for A, B, C, Dz

##### move.kinematics.printRadius (HangprinterKinematics)
Print radius (in mm)

##### move.kinematics.name (HangprinterKinematics)
Currently configured geometry type

##### move.kinematics (Kinematics)
Information about the configured geometry

##### move.kinematics.name (Kinematics)
Currently configured geometry type

##### move.kinematics (ScaraKinematics)
Kinematics class for SCARA kinematics

##### move.kinematics.tiltCorrection (ScaraKinematics)
Parameters describing the tilt correction

##### move.kinematics.tiltCorrection (ScaraKinematics)
Tilt correction parameters for Z leadscrew compensation

##### move.kinematics.tiltCorrection.correctionFactor (ScaraKinematics)
Correction factor

##### move.kinematics.tiltCorrection.lastCorrections[] (ScaraKinematics)
Last corrections (in mm)

##### move.kinematics.tiltCorrection.maxCorrection (ScaraKinematics)
Maximum Z correction (in mm)

##### move.kinematics.tiltCorrection.screwPitch (ScaraKinematics)
Pitch of the Z leadscrews (in mm)

##### move.kinematics.tiltCorrection.screwX[] (ScaraKinematics)
X positions of the leadscrews (in mm)

##### move.kinematics.tiltCorrection.screwY[] (ScaraKinematics)
Y positions of the leadscrews (in mm)

##### move.kinematics.name (ScaraKinematics)
Currently configured geometry type

##### move.kinematics (ZLeadscrewKinematics)
Base kinematics class that provides the ability to level the bed using Z leadscrews

##### move.kinematics.tiltCorrection (ZLeadscrewKinematics)
Parameters describing the tilt correction

##### move.kinematics.tiltCorrection (ZLeadscrewKinematics)
Tilt correction parameters for Z leadscrew compensation

##### move.kinematics.tiltCorrection.correctionFactor (ZLeadscrewKinematics)
Correction factor

##### move.kinematics.tiltCorrection.lastCorrections[] (ZLeadscrewKinematics)
Last corrections (in mm)

##### move.kinematics.tiltCorrection.maxCorrection (ZLeadscrewKinematics)
Maximum Z correction (in mm)

##### move.kinematics.tiltCorrection.screwPitch (ZLeadscrewKinematics)
Pitch of the Z leadscrews (in mm)

##### move.kinematics.tiltCorrection.screwX[] (ZLeadscrewKinematics)
X positions of the leadscrews (in mm)

##### move.kinematics.tiltCorrection.screwY[] (ZLeadscrewKinematics)
Y positions of the leadscrews (in mm)

##### move.kinematics.name (ZLeadscrewKinematics)
Currently configured geometry type

#### move.printingAcceleration
Maximum acceleration allowed while printing (in mm/s^2)

#### move.queue[]
List of move queue items (DDA rings)

##### move.queue[].gracePeriod
The minimum idle time before we should start a move (in s)

##### move.queue[].length
Maximum number of moves that can be accomodated in the DDA ring

#### move.shaping
Parameters for input shaping

##### move.shaping.damping
Damping factor

##### move.shaping.frequency
Frequency (in Hz)

##### move.shaping.minimumAcceleration
Minimum acceleration (in mm/s)

##### move.shaping.type
Configured input shaping type

#### move.speedFactor
Speed factor applied to every regular move (0.01..1 or greater)

#### move.travelAcceleration
Maximum acceleration allowed while travelling (in mm/s^2)

#### move.virtualEPos
Virtual total extruder position

#### move.workplaceNumber
Index of the currently selected workplace

## network
Information about connected network adapters

#### network.corsSite
*This field is maintained by DSF in SBC mode and might not be available in standalone mode*

If this is set, the web server will allow cross-origin requests via the Access-Control-Allow-Origin header

#### network.hostname
Hostname of the machine

#### network.interfaces[]
*This field is maintained by DSF in SBC mode and might not be available in standalone mode*

List of available network interfaces

##### network.interfaces[].activeProtocols[]
List of active protocols

##### network.interfaces[].actualIP
Actual IPv4 address of the network adapter

##### network.interfaces[].configuredIP
Configured IPv4 address of the network adapter

##### network.interfaces[].dnsServer
Configured IPv4 DNS server fo the network adapter

##### network.interfaces[].firmwareVersion
Version of the network interface or null if unknown.
This is primarily intended for the ESP8266-based network interfaces as used on the Duet WiFi

##### network.interfaces[].gateway
IPv4 gateway of the network adapter

##### network.interfaces[].mac
Physical address of the network adapter

##### network.interfaces[].numReconnects
Number of reconnect attempts or null if unknown

##### network.interfaces[].signal
Signal of the WiFi adapter (only WiFi, in dBm, or null if unknown)

##### network.interfaces[].speed
Speed of the network interface (in MBit, null if unknown, 0 if not connected)

##### network.interfaces[].subnet
Subnet of the network adapter

##### network.interfaces[].type
Type of this network interface

#### network.name
Name of the machine

## plugins[]
*This field is maintained by DSF in SBC mode and might not be available in standalone mode*

Dictionary of SBC plugins where each key is the plugin identifier

*Note:* Values in this dictionary cannot become null. If a change to null is reported, the corresponding key is deleted.
Do not rely on the setter of this property; it will be removed from a future version.

### plugins[] (PluginManifest)
Information about a third-party plugin

#### plugins[].id (PluginManifest)
Identifier of this plugin. May consist of letters and digits only (max length 32 chars)

*Note:* For plugins with DWC components, this is the Webpack chunk name too

#### plugins[].name (PluginManifest)
Name of the plugin. May consist of letters, digits, dashes, and underscores only (max length 64 chars)

#### plugins[].author (PluginManifest)
Author of the plugin

#### plugins[].version (PluginManifest)
Version of the plugin

#### plugins[].license (PluginManifest)
License of the plugin. Should follow the SPDX format (see https://spdx.org/licenses/)

#### plugins[].homepage (PluginManifest)
Link to the plugin homepage or source code repository

#### plugins[].dwcVersion (PluginManifest)
Major/minor compatible DWC version

#### plugins[].dwcDependencies[] (PluginManifest)
List of DWC plugins this plugin depends on. Circular dependencies are not supported

#### plugins[].sbcRequired (PluginManifest)
Set to true if a SBC is absolutely required for this plugin

#### plugins[].sbcDsfVersion (PluginManifest)
Required DSF version for the plugin running on the SBC (ignored if there is no SBC executable)

#### plugins[].sbcExecutable (PluginManifest)
Filename in the dsf directory used to start the plugin

*Note:* A plugin may provide different binaries in subdirectories per architecture.
Supported architectures are: arm, arm64, x86, x86_64

#### plugins[].sbcExtraExecutables[] (PluginManifest)
List of other filenames in the dsf directory that should be executable

#### plugins[].sbcExecutableArguments (PluginManifest)
Command-line arguments for the executable

#### plugins[].sbcOutputRedirected (PluginManifest)
Defines if messages from stdout/stderr are output as generic messages

#### plugins[].sbcPermissions (PluginManifest)
List of permissions required by the plugin executable running on the SBC

#### plugins[].sbcPackageDependencies[] (PluginManifest)
List of packages this plugin depends on (apt packages in the case of DuetPi)

#### plugins[].sbcPluginDependencies[] (PluginManifest)
List of SBC plugins this plugin depends on. Circular dependencies are not supported

#### plugins[].rrfVersion (PluginManifest)
Major/minor supported RRF version (optional)

#### plugins[].data[] (PluginManifest)
Custom plugin data to be populated in the object model (DSF/DWC in SBC mode - or - DWC in standalone mode).
Before  can be used, corresponding properties must be registered via this property first!

### plugins[] (Plugin)
Class representing a loaded plugin

#### plugins[].dsfFiles[] (Plugin)
List of files for the DSF plugin

#### plugins[].dwcFiles[] (Plugin)
List of files for the DWC plugin

#### plugins[].sdFiles[] (Plugin)
List of files to be installed to the (virtual) SD excluding web files

#### plugins[].pid (Plugin)
Process ID of the plugin or -1 if not started. It is set to 0 while the plugin is being shut down

#### plugins[].id (Plugin)
Identifier of this plugin. May consist of letters and digits only (max length 32 chars)

*Note:* For plugins with DWC components, this is the Webpack chunk name too

#### plugins[].name (Plugin)
Name of the plugin. May consist of letters, digits, dashes, and underscores only (max length 64 chars)

#### plugins[].author (Plugin)
Author of the plugin

#### plugins[].version (Plugin)
Version of the plugin

#### plugins[].license (Plugin)
License of the plugin. Should follow the SPDX format (see https://spdx.org/licenses/)

#### plugins[].homepage (Plugin)
Link to the plugin homepage or source code repository

#### plugins[].dwcVersion (Plugin)
Major/minor compatible DWC version

#### plugins[].dwcDependencies[] (Plugin)
List of DWC plugins this plugin depends on. Circular dependencies are not supported

#### plugins[].sbcRequired (Plugin)
Set to true if a SBC is absolutely required for this plugin

#### plugins[].sbcDsfVersion (Plugin)
Required DSF version for the plugin running on the SBC (ignored if there is no SBC executable)

#### plugins[].sbcExecutable (Plugin)
Filename in the dsf directory used to start the plugin

*Note:* A plugin may provide different binaries in subdirectories per architecture.
Supported architectures are: arm, arm64, x86, x86_64

#### plugins[].sbcExtraExecutables[] (Plugin)
List of other filenames in the dsf directory that should be executable

#### plugins[].sbcExecutableArguments (Plugin)
Command-line arguments for the executable

#### plugins[].sbcOutputRedirected (Plugin)
Defines if messages from stdout/stderr are output as generic messages

#### plugins[].sbcPermissions (Plugin)
List of permissions required by the plugin executable running on the SBC

#### plugins[].sbcPackageDependencies[] (Plugin)
List of packages this plugin depends on (apt packages in the case of DuetPi)

#### plugins[].sbcPluginDependencies[] (Plugin)
List of SBC plugins this plugin depends on. Circular dependencies are not supported

#### plugins[].rrfVersion (Plugin)
Major/minor supported RRF version (optional)

#### plugins[].data[] (Plugin)
Custom plugin data to be populated in the object model (DSF/DWC in SBC mode - or - DWC in standalone mode).
Before  can be used, corresponding properties must be registered via this property first!

## scanner
*This field is maintained by DSF in SBC mode and might not be available in standalone mode*

Information about the 3D scanner subsystem

#### scanner.progress
Progress of the current action (on a scale between 0 to 1)

*Note:* Previous status responses used a scale of 0..100

#### scanner.status
Status of the 3D scanner

## sensors
Information about connected sensors including Z-probes and endstops

#### sensors.analog[]
List of analog sensors

##### sensors.analog[].lastReading
Last sensor reading (in C) or null if invalid

##### sensors.analog[].name
Name of this sensor or null if not configured

##### sensors.analog[].type
Type of this sensor

#### sensors.endstops[]
List of configured endstops

##### sensors.endstops[].highEnd
Whether this endstop is at the high end of the axis

##### sensors.endstops[].triggered
Whether or not the endstop is hit

##### sensors.endstops[].type
Type of the endstop

#### sensors.filamentMonitors[]
List of configured filament monitors

##### sensors.filamentMonitors[] (FilamentMonitor)
Information about a filament monitor

##### sensors.filamentMonitors[].enabled (FilamentMonitor)
Indicates if this filament monitor is enabled

##### sensors.filamentMonitors[].status (FilamentMonitor)
Last reported status of this filament monitor

##### sensors.filamentMonitors[].type (FilamentMonitor)
Type of this filament monitor

##### sensors.filamentMonitors[] (LaserFilamentMonitor)
Information about a laser filament monitor

##### sensors.filamentMonitors[].calibrated (LaserFilamentMonitor)
Calibrated properties of this filament monitor

##### sensors.filamentMonitors[].calibrated (LaserFilamentMonitor)
Calibrated properties of a laser filament monitor

##### sensors.filamentMonitors[].calibrated.calibrationFactor (LaserFilamentMonitor)
Calibration factor of this sensor

##### sensors.filamentMonitors[].calibrated.percentMax (LaserFilamentMonitor)
Maximum percentage (0..1 or greater)

##### sensors.filamentMonitors[].calibrated.percentMin (LaserFilamentMonitor)
Minimum percentage (0..1)

##### sensors.filamentMonitors[].calibrated.sensivity (LaserFilamentMonitor)
Calibrated sensivity

##### sensors.filamentMonitors[].calibrated.totalDistance (LaserFilamentMonitor)
Total extruded distance (in mm)

##### sensors.filamentMonitors[].configured (LaserFilamentMonitor)
Configured properties of this filament monitor

##### sensors.filamentMonitors[].configured (LaserFilamentMonitor)
Configured properties of a laser filament monitor

##### sensors.filamentMonitors[].configured.percentMax (LaserFilamentMonitor)
Maximum percentage (0..1 or greater)

##### sensors.filamentMonitors[].configured.percentMin (LaserFilamentMonitor)
Minimum percentage (0..1)

##### sensors.filamentMonitors[].configured.sampleDistance (LaserFilamentMonitor)
Sample distance (in mm)

##### sensors.filamentMonitors[].filamentPresent (LaserFilamentMonitor)
Indicates if a filament is present

##### sensors.filamentMonitors[].enabled (LaserFilamentMonitor)
Indicates if this filament monitor is enabled

##### sensors.filamentMonitors[].status (LaserFilamentMonitor)
Last reported status of this filament monitor

##### sensors.filamentMonitors[].type (LaserFilamentMonitor)
Type of this filament monitor

##### sensors.filamentMonitors[] (PulsedFilamentMonitor)
Information about a pulsed filament monitor

##### sensors.filamentMonitors[].calibrated (PulsedFilamentMonitor)
Calibrated properties of this filament monitor

##### sensors.filamentMonitors[].calibrated (PulsedFilamentMonitor)
Calibrated properties of a pulsed filament monitor

##### sensors.filamentMonitors[].calibrated.mmPerPulse (PulsedFilamentMonitor)
Extruded distance per pulse (in mm)

##### sensors.filamentMonitors[].calibrated.percentMax (PulsedFilamentMonitor)
Maximum percentage (0..1 or greater)

##### sensors.filamentMonitors[].calibrated.percentMin (PulsedFilamentMonitor)
Minimum percentage (0..1)

##### sensors.filamentMonitors[].calibrated.totalDistance (PulsedFilamentMonitor)
Total extruded distance (in mm)

##### sensors.filamentMonitors[].configured (PulsedFilamentMonitor)
Configured properties of this filament monitor

##### sensors.filamentMonitors[].configured (PulsedFilamentMonitor)
Configured properties of a pulsed filament monitor

##### sensors.filamentMonitors[].configured.mmPerPulse (PulsedFilamentMonitor)
Extruded distance per pulse (in mm)

##### sensors.filamentMonitors[].configured.percentMax (PulsedFilamentMonitor)
Maximum percentage (0..1 or greater)

##### sensors.filamentMonitors[].configured.percentMin (PulsedFilamentMonitor)
Minimum percentage (0..1)

##### sensors.filamentMonitors[].configured.sampleDistance (PulsedFilamentMonitor)
Sample distance (in mm)

##### sensors.filamentMonitors[].enabled (PulsedFilamentMonitor)
Indicates if this filament monitor is enabled

##### sensors.filamentMonitors[].status (PulsedFilamentMonitor)
Last reported status of this filament monitor

##### sensors.filamentMonitors[].type (PulsedFilamentMonitor)
Type of this filament monitor

##### sensors.filamentMonitors[] (RotatingMagnetFilamentMonitor)
Information about a rotating magnet filament monitor

##### sensors.filamentMonitors[].calibrated (RotatingMagnetFilamentMonitor)
Calibrated properties of this filament monitor

##### sensors.filamentMonitors[].calibrated (RotatingMagnetFilamentMonitor)
Calibrated properties of a rotating magnet filament monitor

##### sensors.filamentMonitors[].calibrated.mmPerRev (RotatingMagnetFilamentMonitor)
Extruded distance per revolution (in mm)

##### sensors.filamentMonitors[].calibrated.percentMax (RotatingMagnetFilamentMonitor)
Maximum percentage (0..1 or greater)

##### sensors.filamentMonitors[].calibrated.percentMin (RotatingMagnetFilamentMonitor)
Minimum percentage (0..1)

##### sensors.filamentMonitors[].calibrated.totalDistance (RotatingMagnetFilamentMonitor)
Total extruded distance (in mm)

##### sensors.filamentMonitors[].configured (RotatingMagnetFilamentMonitor)
Configured properties of this filament monitor

##### sensors.filamentMonitors[].configured (RotatingMagnetFilamentMonitor)
Configured properties of a rotating magnet filament monitor

##### sensors.filamentMonitors[].configured.mmPerRev (RotatingMagnetFilamentMonitor)
Extruded distance per revolution (in mm)

##### sensors.filamentMonitors[].configured.percentMax (RotatingMagnetFilamentMonitor)
Maximum percentage (0..1 or greater)

##### sensors.filamentMonitors[].configured.percentMin (RotatingMagnetFilamentMonitor)
Minimum percentage (0..1)

##### sensors.filamentMonitors[].configured.sampleDistance (RotatingMagnetFilamentMonitor)
Sample distance (in mm)

##### sensors.filamentMonitors[].filamentPresent (RotatingMagnetFilamentMonitor)
Indicates if a filament is present

##### sensors.filamentMonitors[].enabled (RotatingMagnetFilamentMonitor)
Indicates if this filament monitor is enabled

##### sensors.filamentMonitors[].status (RotatingMagnetFilamentMonitor)
Last reported status of this filament monitor

##### sensors.filamentMonitors[].type (RotatingMagnetFilamentMonitor)
Type of this filament monitor

##### sensors.filamentMonitors[] (SimpleFilamentMonitor)
Representation of a simple filament monitor

##### sensors.filamentMonitors[].filamentPresent (SimpleFilamentMonitor)
Indicates if a filament is present

##### sensors.filamentMonitors[].enabled (SimpleFilamentMonitor)
Indicates if this filament monitor is enabled

##### sensors.filamentMonitors[].status (SimpleFilamentMonitor)
Last reported status of this filament monitor

##### sensors.filamentMonitors[].type (SimpleFilamentMonitor)
Type of this filament monitor

#### sensors.gpIn[]
List of general-purpose input ports

##### sensors.gpIn[].value
Value of this port (0..1)

#### sensors.probes[]
List of configured probes

##### sensors.probes[].calibrationTemperature
Calibration temperature (in C)

##### sensors.probes[].deployedByUser
Indicates if the user has deployed the probe

##### sensors.probes[].disablesHeaters
Whether probing disables the heater(s)

##### sensors.probes[].diveHeight
Dive height (in mm)

##### sensors.probes[].lastStopHeight
Height of the probe where it stopped last time (in mm)

##### sensors.probes[].maxProbeCount
Maximum number of times to probe after a bad reading was determined

##### sensors.probes[].offsets[]
X+Y offsets (in mm)

##### sensors.probes[].recoveryTime
Recovery time (in s)

##### sensors.probes[].speeds[]
Fast and slow probing speeds (in mm/s)

##### sensors.probes[].temperatureCoefficients[]
List of temperature coefficients

##### sensors.probes[].threshold
Configured trigger threshold (0..1023)

##### sensors.probes[].tolerance
Allowed tolerance deviation between two measures (in mm)

##### sensors.probes[].travelSpeed
Travel speed when probing multiple points (in mm/s)

##### sensors.probes[].triggerHeight
Z height at which the probe is triggered (in mm)

##### sensors.probes[].type
Type of the configured probe

##### sensors.probes[].value[]
Current analog values of the probe

## spindles[]
List of configured CNC spindles

#### spindles[].active
Active RPM

#### spindles[].current
Current RPM, negative if anticlockwise direction

#### spindles[].frequency
Frequency (in Hz)

#### spindles[].min
Minimum RPM when turned on

#### spindles[].max
Maximum RPM

#### spindles[].state
Current state

## state
Information about the machine state

#### state.atxPower
State of the ATX power pin (if controlled)

#### state.beep
Information about a requested beep or null if none is requested

##### state.beep.duration
Duration of the requested beep (in ms)

##### state.beep.frequency
Frequency of the requested beep (in Hz)

#### state.currentTool
Number of the currently selected tool or -1 if none is selected

#### state.displayMessage
Persistent message to display (see M117)

#### state.dsfVersion
*This field is maintained by DSF in SBC mode and might not be available in standalone mode*

Version of the Duet Software Framework package

#### state.dsfPluginSupport
*This field is maintained by DSF in SBC mode and might not be available in standalone mode*

Indicates if DSF allows the installation and usage of third-party plugins

#### state.dsfRootPluginSupport
*This field is maintained by DSF in SBC mode and might not be available in standalone mode*

Indicates if DSF allows the installation and usage of third-party root plugins (potentially dangerous)

#### state.gpOut[]
List of general-purpose output ports

##### state.gpOut[].pwm
PWM value of this port (0..1)

#### state.laserPwm
Laser PWM of the next commanded move (0..1) or null if not applicable

#### state.logFile
*This field is maintained by DSF in SBC mode and might not be available in standalone mode*

Log file being written to or null if logging is disabled

#### state.logLevel
*This field is maintained by DSF in SBC mode and might not be available in standalone mode*

Current log level

#### state.messageBox
Details about a requested message box or null if none is requested

##### state.messageBox.axisControls
Bitmap of the axis movement controls to show (indices)

##### state.messageBox.message
Content of the message box

##### state.messageBox.mode
Mode of the message box to display

##### state.messageBox.seq
Sequence number of the message box

*Note:* This is increased whenever a new message box is supposed to be displayed

##### state.messageBox.timeout
Total timeout for this message box (in ms)

##### state.messageBox.title
Title of the message box

#### state.machineMode
Current mode of operation

#### state.msUpTime
Millisecond fraction of

#### state.nextTool
Number of the next tool to be selected

#### state.pluginsStarted
Indicates if at least one plugin has been started

#### state.powerFailScript
Script to execute when the power fails

#### state.previousTool
Number of the previous tool

#### state.restorePoints[]
List of restore points

##### state.restorePoints[].coords[]
Axis coordinates of the restore point (in mm)

##### state.restorePoints[].extruderPos
The virtual extruder position at the start of this move

##### state.restorePoints[].fanPwm
PWM value of the tool fan (0..1)

##### state.restorePoints[].feedRate
Requested feedrate (in mm/s)

##### state.restorePoints[].ioBits
The output port bits setting for this move or null if not applicable

##### state.restorePoints[].laserPwm
Laser PWM value (0..1) or null if not applicable

##### state.restorePoints[].spindleSpeeds[]
The spindle RPMs that were set, negative if anticlockwise direction

##### state.restorePoints[].toolNumber
The tool number that was active

#### state.status
Current state of the machine

#### state.time
Internal date and time in RepRapFirmware or null if unknown

#### state.upTime
How long the machine has been running (in s)

## tools[]
List of configured tools

#### tools[].active[]
Active temperatures of the associated heaters (in C)

#### tools[].axes[]
Associated axes. At present only X and Y can be mapped per tool.

*Note:* The order is the same as the visual axes, so by default the layout is
[
[0],        // X
[1]         // Y
]
Make sure to set each item individually so the change events are called

#### tools[].extruders[]
Extruder drives of this tool

#### tools[].fans[]
List of associated fans (indices)

#### tools[].filamentExtruder
Extruder drive index for resolving the tool filament (index or -1)

#### tools[].heaters[]
List of associated heaters (indices)

#### tools[].mix[]
Mix ratios of the associated extruder drives

#### tools[].name
Name of this tool

#### tools[].number
Number of this tool

#### tools[].offsets[]
Axis offsets (in mm)
This list is in the same order as

#### tools[].offsetsProbed
Bitmap of the probed axis offsets

#### tools[].retraction
Firmware retraction parameters

##### tools[].retraction.extraRestart
Amount of additional filament to extrude when undoing a retraction (in mm)

##### tools[].retraction.length
Retraction length (in mm)

##### tools[].retraction.speed
Retraction speed (in mm/s)

##### tools[].retraction.unretractSpeed
Unretract speed (in mm/s)

##### tools[].retraction.zHop
Amount of Z lift after doing a retraction (in mm)

#### tools[].spindle
Index of the mapped spindle or -1 if not mapped

#### tools[].spindleRpm
RPM of the mapped spindle

#### tools[].standby[]
Standby temperatures of the associated heaters (in C)

#### tools[].state
Current state of this tool

## userSessions[]
*This field is maintained by DSF in SBC mode and might not be available in standalone mode*

List of user sessions

#### userSessions[].id
Identifier of this session

#### userSessions[].accessLevel
Access level of this session

#### userSessions[].sessionType
Type of this sessionSessionAccessLevel

#### userSessions[].origin
Origin of this session. For remote sessions, this equals the remote IP address

#### userSessions[].originId
Corresponding identifier of the origin.
If it is a remote session, it is the remote port, else it defaults to the PID of the current process

## volumes[]
*This field is maintained by DSF in SBC mode and might not be available in standalone mode*

List of available mass storages

#### volumes[].capacity
Total capacity of the storage device (in bytes or null)

#### volumes[].freeSpace
How much space is still available on this device (in bytes or null)

#### volumes[].mounted
Whether the storage device is mounted

#### volumes[].name
Name of this volume

#### volumes[].openFiles
Number of currently open files or null if unknown

#### volumes[].path
Logical path of the storage device

#### volumes[].speed
Speed of the storage device (in bytes/s or null if unknown)

## Disclaimer

This file is auto-generated from the DSF code documentation. If you find errors, please report them on the [forum](https://forum.duet3d.com).
