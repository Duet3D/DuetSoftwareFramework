# RRF 3.7 GCode coverage

Every GCode command documented for RepRapFirmware 3.7, with three empty columns for tracking how
far DSF covers each one.

Source: [Duet3D wiki GCode dictionary](https://github.com/Duet3D/wiki-content/blob/master/User_manual/Reference/Gcodes.md),
page revision dated 2026-09-10, retrieved 2026-09-14.

How to read the table:

* **Parameters** lists the parameter letters documented for RRF 3.7. Where the wiki documents
  several firmware versions, only the newest set is listed. `none` means the command takes no
  parameters.
* **Description** is a one line summary. Command numbers that have more than one documented form
  (G10, G38.x) get one row per form.
* **Regression tested** means the command is covered by regression tests in https://github.com/Duet3D/DuetRegressionTesting.
* **System tested** means the command is covered by a system test in [src/SystemTests](../../src/SystemTests).
* **Fully implemented** means every documented parameter behaves as RRF 3.7 does.

| Command | Parameters | Description | Regression tested | System tested | Fully implemented |
| --- | --- | --- | --- | --- | --- |
| `G0` | X Y Z E F H S R P | Rapid move; as G1 but run at the maximum feedrate in CNC and laser mode | | | |
| `G1` | X Y Z E F H S R P | Controlled linear move | | | |
| `G2` | X Y Z I J K E F R S | Clockwise arc move | | | |
| `G3` | X Y Z I J K E F R S | Counter-clockwise arc move | | | |
| `G4` | P S | Wait for the specified time before executing the next command | | | |
| `G10` | P R S | Set the active and standby temperatures of a tool | | | |
| `G10` | L P X Y U V W A B C D Z | Set workplace coordinate offset or tool offset | | | |
| `G10` | none | Retract filament using the M207 settings (the form with no parameters) | | | |
| `G11` | none | Unretract | | | |
| `G17` | none | Select XY plane for arc moves | | | |
| `G18` | none | Select XZ plane for arc moves | | | |
| `G19` | none | Select YZ plane for arc moves | | | |
| `G20` | none | Set Units to Inches | | | |
| `G21` | none | Set Units to Millimeters | | | |
| `G28` | X Y Z U V W A B C D | Home the given axes, or all axes if none are given, by running homing macros | | | |
| `G29` | S P K | Mesh bed probe | | | |
| `G30` | P X Y Z H S K | Single Z-Probe | | | |
| `G31` | K P Z X Y U V W A B C S T H | Set or Report Current Probe status | | | |
| `G32` | none | Run bed.g, normally for bed levelling or delta auto calibration | | | |
| `G38.2` | X Y Z U V W A B C P K F | Probe toward workpiece, stop on contact, signal error if the probe does not trigger | | | |
| `G38.3` | X Y Z U V W A B C P K F | Probe toward workpiece, stop on contact | | | |
| `G38.4` | X Y Z U V W A B C P K F | Probe away from workpiece, stop on loss of contact, signal error if it stays triggered | | | |
| `G38.5` | X Y Z U V W A B C P K F | Probe away from workpiece, stop on loss of contact | | | |
| `G53` | none | Interpret the coordinates on the rest of the line as machine coordinates | | | |
| `G54` | none | Select workplace coordinate system 1 | | | |
| `G55` | none | Select workplace coordinate system 2 | | | |
| `G56` | none | Select workplace coordinate system 3 | | | |
| `G57` | none | Select workplace coordinate system 4 | | | |
| `G58` | none | Select workplace coordinate system 5 | | | |
| `G59` | none | Select workplace coordinate system 6 | | | |
| `G59.1` | none | Select workplace coordinate system 7 | | | |
| `G59.2` | none | Select workplace coordinate system 8 | | | |
| `G59.3` | none | Select workplace coordinate system 9 | | | |
| `G60` | S | Save current position to slot | | | |
| `G68` | X Y A B R | Coordinate rotation | | | |
| `G69` | none | Cancel coordinate rotation | | | |
| `G90` | none | Set to Absolute Positioning | | | |
| `G91` | none | Set to Relative Positioning | | | |
| `G92` | X Y Z E | Set the current user position without moving | | | |
| `G93` | none | Feed Rate Mode (Inverse Time Mode) | | | |
| `G94` | none | Feed Rate Mode (Units per Minute) | | | |
| `M0` | none | Stop or Unconditional stop | | | |
| `M1` | none | Sleep or Conditional stop | | | |
| `M2` | none | End the job, currently the same as M0 | | | |
| `M3` | S P | Spindle On, Clockwise | | | |
| `M4` | S P | Spindle On, Counterclockwise | | | |
| `M5` | none | Spindle Off | | | |
| `M17` | X Y Z U V W E | Enable motors | | | |
| `M18` | X Y Z U V W E | Disable motors | | | |
| `M20` | S P R C | List SD card | | | |
| `M21` | P S T O | Initialize SD card | | | |
| `M22` | P | Release SD card | | | |
| `M23` | none | Select a file on the SD card ready to print | | | |
| `M24` | none | Start/resume SD print | | | |
| `M25` | none | Pause SD print | | | |
| `M26` | S C P X Y Z | Set SD position | | | |
| `M27` | none | Report SD print status | | | |
| `M28` | none | Begin write to SD card | | | |
| `M29` | none | Stop writing to SD card | | | |
| `M30` | none | Delete a file on the SD card | | | |
| `M32` | none | Select file and start SD print | | | |
| `M36` | none | Return file information | | | |
| `M36.1` | P S | Return embedded thumbnail data | | | |
| `M36.2` | P S | Return height map data | | | |
| `M37` | P F S | Enable or disable simulation mode, or simulate a file to estimate its print time | | | |
| `M38` | none | Compute CRC32 hash of target file | | | |
| `M39` | P S | Report SD card information | | | |
| `M42` | P S | Switch I/O pin | | | |
| `M73` | P R C | Set remaining print time | | | |
| `M80` | C | ATX Power On | | | |
| `M81` | C S D | ATX Power Off | | | |
| `M82` | none | Set extruder to absolute mode | | | |
| `M83` | none | Set extruder to relative mode | | | |
| `M84` | S X Y E | Stop idle hold (deprecated in RRF 3.6 and later, use M18 and M906 T) | | | |
| `M92` | X Y Z U V W E S | Set axis steps per unit | | | |
| `M98` | P R | Call Macro/Subprogram | | | |
| `M99` | none | Return from Macro/Subprogram | | | |
| `M101` | none | Un-retract filament | | | |
| `M102` | none | Accepted and ignored, for Simplify3D compatibility | | | |
| `M103` | none | Retract filament | | | |
| `M104` | S T | Set Extruder Temperature | | | |
| `M105` | R S | Get Extruder Temperature | | | |
| `M106` | P S L X B H R T C | Fan On | | | |
| `M107` | none | Fan Off (deprecated, use M106 S0) | | | |
| `M108` | none | Cancel Heating | | | |
| `M109` | S R T | Set Extruder Temperature and Wait | | | |
| `M110` | N | Set the current line number used for checksum and line number checking | | | |
| `M111` | P S D B F O | Set Debug Level | | | |
| `M112` | none | Emergency Stop | | | |
| `M114` | none | Get Current Position | | | |
| `M115` | B P | Get Firmware Version and Capabilities | | | |
| `M116` | P H C S | Wait for temperature to be reached | | | |
| `M117` | none | Display a message on the attached display or in the web interface | | | |
| `M118` | P S L T Q R D | Send Message to Specific Target | | | |
| `M119` | none | Get Endstop Status | | | |
| `M120` | none | Push machine state (feed rate, extruder positions, axis and extruder relative flags) onto a stack | | | |
| `M121` | none | Pop the last machine state pushed onto the stack | | | |
| `M122` | P B | Report diagnostic information about the main board or an expansion board | | | |
| `M140` | P H S R | Set Bed Temperature (Fast) or Configure Bed Heater | | | |
| `M141` | P H S R | Set Chamber Temperature (Fast) or Configure Chamber Heater | | | |
| `M143` | H S P T A C | Maximum heater temperature | | | |
| `M144` | P S | Put the bed heater into standby, or switch it back to active | | | |
| `M150` | R U B W P Y S F E | Set LED colours | | | |
| `M190` | S P R | Wait for bed temperature to reach target temp | | | |
| `M191` | S R P | Wait for chamber temperature to reach target temp | | | |
| `M200` | D S | Set filament diameter to interpret extrusion amounts as volumes, or disable volumetric extrusion | | | |
| `M201` | X Y Z U V W E T | Set max acceleration | | | |
| `M201.1` | X Y Z E | Set acceleration for special move types | | | |
| `M203` | X Y Z U V W E S I | Set maximum feedrate | | | |
| `M204` | P T S | Set printing and travel accelerations | | | |
| `M205` | X Y Z U V W | Set max instantaneous speed change in mm/sec | | | |
| `M206` | X Y Z U V W | Offset axes (deprecated, use G10 L2 P1) | | | |
| `M207` | P S R F T Z | Set retract length | | | |
| `M208` | S X Y Z | Set axis max travel | | | |
| `M220` | S | Set speed factor override percentage | | | |
| `M221` | S D | Set extrude factor override percentage | | | |
| `M226` | none | Pause the job from within the job file once all queued moves are done | | | |
| `M260` | A R B S V | i2c Send and/or request Data | | | |
| `M260.1` | P A F R B S | Modbus write registers or coils | | | |
| `M260.2` | P B S | UART write | | | |
| `M260.3` | P B S | Write to Nordson Ultimus V | | | |
| `M260.4` | P A R B S V | Raw Modbus transaction | | | |
| `M261` | A B V | i2c Request Data (deprecated, use M260) | | | |
| `M261.1` | P A R B F V | Modbus read registers, coils or inputs | | | |
| `M261.2` | P B V | UART read | | | |
| `M280` | P S | Set servo position | | | |
| `M290` | S Z X Y U R | Apply a baby stepping offset to the Z axis or other axes while printing | | | |
| `M291` | P R S T X Y Z J K L H F | Display message and optionally wait for response | | | |
| `M292` | P R S | Acknowledge blocking message | | | |
| `M300` | S P C | Play beep sound | | | |
| `M302` | P S R | Allow or forbid extrusion below the cold extrude temperature | | | |
| `M303` | H P S T A Y F Q | Run heater tuning | | | |
| `M307` | H R D E K B I S V | Set or report heating process parameters | | | |
| `M308` | S P Y A T B C R L H W F K | Set or report sensor parameters | | | |
| `M309` | P S T | Set or report heater feedforward | | | |
| `M350` | X Y Z E I | Set microstepping mode | | | |
| `M374` | P | Save height map | | | |
| `M375` | P | Load height map | | | |
| `M376` | H | Set bed compensation taper | | | |
| `M400` | S | Wait for current moves to finish | | | |
| `M401` | P | Deploy z-probe | | | |
| `M402` | P | Retract z-probe | | | |
| `M404` | N D | Filament diameter (deprecated in RRF 3.6 and later, use M200) | | | |
| `M408` | S R | Report JSON-style response (deprecated in RRF 3.3 and later, use M409) | | | |
| `M409` | K F R I | Query object model | | | |
| `M425` | X Y Z A B S | Configure backlash compensation | | | |
| `M450` | none | Report Printer Mode | | | |
| `M451` | none | Select FFF Printer Mode | | | |
| `M452` | C R S F | Select Laser Device Mode | | | |
| `M453` | none | Select CNC Device Mode | | | |
| `M470` | P | Create Directory on SD-Card | | | |
| `M471` | S T D | Rename File/Directory on SD-Card | | | |
| `M472` | P R | Delete File/Directory on SD-Card | | | |
| `M486` | T S A P U C | Name, number and cancel individual objects in the job file | | | |
| `M500` | P | Store parameters | | | |
| `M501` | none | Read stored parameters | | | |
| `M502` | none | Revert stored parameters | | | |
| `M503` | none | Print settings | | | |
| `M505` | P | Set configuration file folder | | | |
| `M505.1` | P | Set HTTP server root folder | | | |
| `M540` | P | Set MAC address | | | |
| `M550` | P | Set Name | | | |
| `M551` | P | Set Password | | | |
| `M552` | I P S R | Set IP address, enable/disable network interface | | | |
| `M553` | I P | Set Netmask | | | |
| `M554` | I P S | Set Gateway and/or DNS server | | | |
| `M555` | P | Set the firmware whose output format is emulated in replies | | | |
| `M556` | S X Y Z P | Axis skew compensation | | | |
| `M557` | X Y U V W A B C R S P | Set Z probe point or define probing grid | | | |
| `M558` | K P C H F T R A S B V U | Create or modify probe | | | |
| `M558.1` | K S A B C | Calibrate, set or report height vs reading of scanning Z probe | | | |
| `M558.2` | K S R | Calibrate, set or report drive current and reading offset for scanning Z probe | | | |
| `M558.3` | K S F H V | Set touch mode parameters for analog probe | | | |
| `M558.4` | K | Tare load cell probe | | | |
| `M559` | P S C | Upload a file to the SD card, by default /sys/config.g | | | |
| `M560` | P S C | Upload a file to the SD card, by default /www/reprap.htm | | | |
| `M561` | none | Cancel any bed compensation or bed plane fit currently in use | | | |
| `M562` | P | Reset temperature fault | | | |
| `M563` | P S D H F X Y Z L R | Define or remove a tool | | | |
| `M564` | H S R | Allow or forbid moves outside the axis limits and moves before homing | | | |
| `M566` | X Y Z E P | Set allowable instantaneous speed change | | | |
| `M567` | P E | Set tool mix ratios | | | |
| `M568` | P R S F | Set Tool Settings | | | |
| `M569` | P S R T D F C B H Y V U | Set motor driver direction, enable polarity, mode and step pulse timing | | | |
| `M569.1` | P T C E S R I D V A B H Q Y | Stepper driver closed loop configuration | | | |
| `M569.2` | P R V S J O | Read or write stepper driver register | | | |
| `M569.3` | P S | Read Motor Driver Encoder via secondary CAN bus | | | |
| `M569.4` | P T V | Set Motor Driver Torque Mode | | | |
| `M569.5` | P F R D V S T | Closed loop data collection | | | |
| `M569.6` | P V | Execute closed loop tuning move | | | |
| `M569.7` | P C S V | Configure motor brake port | | | |
| `M569.8` | P | Read motor force via secondary CAN bus | | | |
| `M569.9` | P R S | Configure driver sense resistor and maximum current | | | |
| `M570` | H P T R | Configure heater fault detection | | | |
| `M571` | S F Q P | Set output on extrude | | | |
| `M572` | D S L | Set or report extruder pressure advance | | | |
| `M574` | X Y Z E P S K | Set endstop configuration | | | |
| `M575` | P B C S F | Set serial comms parameters | | | |
| `M576` | S F P B D | Set SPI comms parameters | | | |
| `M577` | S X Y Z U V W A B C D P | Wait until endstop is triggered | | | |
| `M579` | X Y Z U V W A B C | Scale Cartesian axes | | | |
| `M581` | T R P X Y Z S | Configure external trigger on inputs and/or endstops | | | |
| `M581.1` | T P R | Configure external trigger on expression | | | |
| `M582` | T S | Check the trigger condition of an external trigger and run its macro if it is active | | | |
| `M584` | X Y Z E U V W A R S P | Set drive mapping | | | |
| `M585` | X Y Z U V W A B C P F S R | Probe Tool | | | |
| `M586` | I P S H R T C | Configure network protocols | | | |
| `M586.4` | U K C W T Q R S | Configure MQTT Client | | | |
| `M587` | S P I J K L C F X E A U Q | Add WiFi host network to remembered list, or list remembered networks | | | |
| `M587.1` | none | Start network scan | | | |
| `M587.2` | F | Return network scan results | | | |
| `M588` | S | Forget WiFi host network | | | |
| `M589` | S P I C | Configure access point parameters | | | |
| `M591` | D P C S R E L A | Configure filament sensing | | | |
| `M592` | D A B L T | Configure nonlinear extrusion | | | |
| `M593` | P F S L H T | Configure Input Shaping | | | |
| `M594` | P | Enter or leave height following mode, in which Z follows a sensor reading | | | |
| `M595` | P S R Q | Set movement queue length | | | |
| `M596` | P | Select movement queue number | | | |
| `M597` | X Y U V | Define a minimum separation between two axes to avoid collisions | | | |
| `M598` | none | Wait for all motion systems to reach this point before continuing | | | |
| `M599` | P S X Y | Define keepout zone | | | |
| `M600` | none | Filament change pause | | | |
| `M606` | S | Fork the job file reader so each motion system runs its own copy of the job | | | |
| `M650` | none | Set the peel move parameters sent by nanoDLP | | | |
| `M651` | none | Run the peel move macro requested by nanoDLP | | | |
| `M655` | B C A P R E | Send request to custom CAN-connected expansion board | | | |
| `M665` | L R B H X Y Z | Set delta configuration | | | |
| `M666` | X Y Z A B | Set delta endstop adjustment | | | |
| `M669` | K X Y Z U V S T P D A B C R F H | Set kinematics type and kinematics parameters | | | |
| `M670` | P C T | Set IO port bit mapping | | | |
| `M671` | X Y S P F | Define positions of Z pivot points or bed levelling screws | | | |
| `M672` | S K | Program the Duet Smart Effector or a similar programmable Z probe | | | |
| `M673` | U V W A B C P | Align plane on rotary axis | | | |
| `M675` | X Y Z F R P | Find center of cavity | | | |
| `M701` | S P | Load filament | | | |
| `M702` | P | Unload filament | | | |
| `M703` | none | Configure filament | | | |
| `M750` | none | Enable 3D scanner extension | | | |
| `M751` | none | Register 3D scanner extension over USB | | | |
| `M752` | S R N P | Start 3D scan | | | |
| `M753` | none | Cancel current 3D scanner action | | | |
| `M754` | N | Calibrate 3D scanner | | | |
| `M755` | P | Set alignment mode for 3D scanner | | | |
| `M756` | none | Shutdown 3D scanner | | | |
| `M851` | Z | Set Z-Probe Offset (Marlin Compatibility) | | | |
| `M905` | P S T A | Set local date and time | | | |
| `M906` | X Y Z E I T | Set motor currents | | | |
| `M911` | S R P | Configure auto save on loss of power | | | |
| `M912` | P S | Set electronics temperature monitor adjustment | | | |
| `M913` | X Y Z E | Set motor percentage of normal current | | | |
| `M915` | P X Y Z U V W A B C E S F H T R | Configure motor stall detection | | | |
| `M916` | none | Resume print after power failure | | | |
| `M917` | X Y Z E | Set motor standstill current reduction | | | |
| `M918` | P E F C R | Configure direct-connect display | | | |
| `M929` | P S | Start/stop event logging to SD card | | | |
| `M950` | H F J P S R D E C Q T B L K U | Create heater, fan, spindle, LED strip or GPIO/servo pin | | | |
| `M951` | H P I D F Z | Set height following mode parameters | | | |
| `M952` | B S T J | Set board CAN address and/or base data rate | | | |
| `M953` | S R T J C | Enable CAN and set fast data rate | | | |
| `M954` | A | Configure as CAN expansion board and enable CAN | | | |
| `M955` | P C I S R Q | Configure Accelerometer | | | |
| `M956` | P S X F | Collect accelerometer data and write to file | | | |
| `M957` | E D B P S | Raise an event as if it had been reported by the firmware, for testing event handling | | | |
| `M959` | B T | Configure CAN expansion board behaviour | | | |
| `M970` | X Y Z E | Enable/disable phase stepping | | | |
| `M970.1` | X Y Z E | Configure phase stepping velocity constant | | | |
| `M970.2` | X Y Z E | Configure phase stepping acceleration constant | | | |
| `M970.3` | P S J O | Configure phase stepping waveform correction | | | |
| `M997` | S B P F V | Perform in-application firmware update | | | |
| `M998` | P | Request a resend of a line that failed its checksum or line number check | | | |
| `M999` | B P | Restart the firmware, optionally into the bootloader | | | |
| `T` | nnn R P T | Select a tool, running the tool change macros unless suppressed | | | |

## Commands on the wiki page that RRF 3.7 does not support

These are documented on the same page but are out of scope for RRF 3.7, either because support has
been withdrawn or because they exist only in builds DSF does not target.

| Command | Description | Reason |
| --- | --- | --- |
| `M135` | Set PID sample interval | available only in RRF 2.02 and earlier |
| `M301` | Set PID parameters | support discontinued from RRF 3.7 |
| `M304` | Set PID parameters - Bed | support discontinued from RRF 3.7 |
| `M305` | Set temperature sensor parameters | replaced by M308 in RRF 3 |
| `M568` | Turn off/on tool mix ratios (deprecated) | no longer required or supported since firmware 1.19 |
| `M573` | Report heater PWM | not supported in RRF 3.4 and later |
| `M578` | Fire inkjet bits | only in builds with SUPPORT_INKJET enabled |
| `M580` | Select Roland | only in builds with SUPPORT_ROLAND enabled |
| `M667` | Select CoreXY or related mode | removed in RRF 3.5 and later |
| `M674` | Set Z to center point | not implemented |
| `M914` | Set/Get Expansion Voltage Level Translator | Alligator build only |
