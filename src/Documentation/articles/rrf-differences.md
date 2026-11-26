# Where DSF deliberately differs from RepRapFirmware

DSF's machine control is a port of RepRapFirmware. This lists only the places where DSF is meant to behave differently.

---

## 1. Architecture

- All drivers, heaters, fans, sensors and ports are on CAN expansion boards. Board 0 runs DuetCANMaster and has none.
- A port has to name an expansion board: `io1.in` and `0.io1.in` are refused.
- Not supported: `M954` (DSF is always the main board), `M576` (there is no DSF to RepRapFirmware SPI link), IOBits (`M670`) and main board stall detection. `M950 D` is refused, as RepRapFirmware refuses it in SBC mode.
- `M950 R` takes its ports on an expansion board, where RepRapFirmware requires the main board. Each port is created as an output, numbered down from the top of `state.gpOut[]`, and the board in front of the first port applies to the others.
- `boards[0]` describes DuetCANMaster: `maxMotors` and `maxHeaters` are 0, `supportsDirectDisplay` is false, `timeout` is null, `firmwareFileName` is `DuetCANMaster_<board>.bin`, and `state` is the state of the link to it.
- The SBC's network belongs to DuetPiManagementPlugin, which answers `M552`, `M553`, `M554`, `M587`, `M588` and `M589`. Without it they reply `reserved - requires DuetPiManagementPlugin`.
- The machine name is the Linux hostname. DuetPiManagementPlugin changes the hostname for `M550 P`; without it, `M550 P` takes only a name with the same letters and digits as the hostname.

## 2. Endstops and probes

[Endstops](endstops.md) describes the path in full.

- DuetCANMaster decides that an endstop stops a move, and DuetControlServer corrects the position from the time the switch fired.
- A switch reports every change, where RepRapFirmware limits it to one report in 30 ms.

### 2.1 Homing several axes in one move

- Axes whose drives are coupled stop together, and separate groups stop separately: `G1 H1 X100 U100` on a CoreXYUV homes both pairs. RepRapFirmware stops every drive at the first switch.
- Axes that share a drive cannot be homed in the same move.

### 2.2 Motor stall probe

- `M558 P10` works with drivers on expansion boards, where RepRapFirmware's never triggers.

### 2.3 Endstop state and reports

- `M119` and homing use the state the boards report as it changes. No board is asked before a homing move.
- `M574` reports a switch from the port it was given, without asking its board.
- `M574` on an existing endstop recreates its monitor without deleting it first.

### 2.4 Probe report rate

- A probe's board reports it every 25 ms, and every 2 ms only while a move may stop on it, including a homing move that uses it as an endstop. RepRapFirmware starts at 2 ms and slows down after the first probing move.

## 3. The object model has to be able to recreate the machine

Every setting is in the object model, even when the board is what acts on it. RepRapFirmware does not report these:

- Ports: `fans[].port`, `spindles[].port`, `state.gpOut[].port`, `sensors.gpIn[].port`, `sensors.endstops[].port`, `sensors.probes[].port` and `heat.heaters[].port`.
- Outputs and heaters: `state.gpOut[].servo`, `ledStrips[].frequency`, `heat.heaters[].frequency` and `.ambientSensor`.
- Sensors: `sensors.probes[].sensor`, and the rest of `M308` for an expansion board's sensor.
- Drivers: `boards[].drivers[].config`, with its `stallDetection`, `chopperControl`, `closedLoop`, `brake` and `waveformCorrection`.
- Drives: `move.axes[].phaseStepKv`, `.phaseStepKa`, `.rotational` and `.continuousRotation`, `move.extruders[].phaseStepKv`, `.phaseStepKa` and `.endstopInput`, and `move.jerkPolicy`.
- Heat: `heat.coldExtrusionAllowed`, `.minExtrusionTemperature` and `.minRetractionTemperature`.
- Kinematics: the parameters of a rotary delta and a five-bar SCARA.

These are reported differently:

- `sensors.analog[].type` of an expansion board's sensor is its own type, where RepRapFirmware reports `remote`, and `offsetAdj` and `slopeAdj` are what `M308` set, where RepRapFirmware reports 0.
- `move.compensation.probeGrid` is exact, where RepRapFirmware rounds to one decimal place.
- `move.kinematics.anchors` of a hangprinter has one entry per anchor, where RepRapFirmware always has eight.
- `move.kinematics.ttSpeedMax` and `.ttAccMax` of a polar machine are in degrees, where RepRapFirmware uses internal units.

### 3.1 What DSF refuses and RepRapFirmware accepts

- `M584` naming a driver that another drive uses.
- `M584` naming a driver its board does not have, for an axis or an extruder. RepRapFirmware drops an axis' driver and does not check an extruder's.
- `M584` with too many drivers for one drive, or more axes or drives than the machine can have. Nothing of the code is applied.
- `M574` with more switches than an axis may have drivers.
- `M669 N` outside 3 to 8 on a hangprinter.
- Any bad parameter of `M669`. Nothing of the code is applied, where RepRapFirmware may already have selected the geometry.
- `M572 D` and `M915 E` with an extruder that does not exist. Nothing of the code is applied.
- A negative value for an extruder in `M92`, `M201`, `M203`, `M205`, `M350`, `M566`, `M906`, `M913` or `M917`.
- `M20 S`, `M39 S` and `M581.1 R` with a value that selects nothing. RepRapFirmware lists or reports as text, or keeps a trigger that never fires.

DSF accepts one thing RepRapFirmware does not: `M584 X` with no driver releases the drivers of X. The axis stays.

## 4. Events

- `controller_disconnect` and `controller_reconnect` are raised when the link to DuetCANMaster drops and returns. They run `controller-disconnect.g` and `controller-reconnect.g`, and are numbered 128 and 129.
- The default action of `controller_reconnect` is to run `config.g`. The default action of `controller_disconnect` is to log it.
- `M957` may raise either. It does not touch the link.
- At most 64 events are queued. The one with the lowest priority is dropped with a warning.

## 5. Interpreter and move path

- `config-override.g` (`M501`) and `dsf-config.g` keep the interpreter state they finish with, as `config.g` does.
- `G10 L2` and `G10 L20` take a rotary axis' origin in degrees under `G20`.
- A faulted heater on an expansion board blocks extrusion.

### 5.1 There are no default axes, so X, Y and Z are found by letter

- `move.axes[]` is empty until `M584` creates an axis, and X, Y and Z may be anywhere in it or absent.
- Kinematics take their axes by letter, where RepRapFirmware takes them by position:
  - CoreXY, MarkForged, SCARA, five-bar SCARA and polar: X and Y. A SCARA's crosstalk acts on Z if there is one.
  - CoreXZ: X and Z.
  - CoreXYU and CoreXYUV: X, Y, U and V.
  - Linear delta: X, Y and Z, then U, V and W for more towers.
  - Rotary delta: X, Y and Z.
  - Hangprinter: X, Y and Z, then U, V, W, A and B for more anchors.
- `M669`, `M665` and `M666` are refused if an axis the geometry needs does not exist, so `M584` comes first.
- Core matrices: a row for an axis the machine does not have is refused, where RepRapFirmware ignores it. A matrix that cannot be inverted is refused, where RepRapFirmware reports it and carries on.
- `M290 S` is an error on a machine without Z.
- `M851 Z` writes the offset of the axis lettered Z, where RepRapFirmware writes axis 2.
- `M584 P` hides the axes created last.

### 5.2 Probing moves and skew

- `G30` applies the skew correction to its dive, as `G29` does.
- After a probing move the position is read back with no tool axis map, as the move was made.

### 5.3 The move back on resume

- It is measured and skewed through the selected tool's axis map, so the `M204` limits apply to the axes that move.

### 5.4 Temperature waits in a macro

- `M116`, `M190` and `M191` keep a movement lock held by the code that ran the macro, as `M109` does.

### 5.5 M108

- It ends a temperature wait from any input, including the one that is waiting.

## 6. G-code and expressions

### 6.1 Error handling

- `M140` and `M141` change nothing when `H` is refused. RepRapFirmware goes on to apply `S` and `R`.
- A refused `M207` changes nothing. RepRapFirmware keeps the values read before the refused one, and stops at the first tool.
- `M207` checks its parameters when there are no tools. RepRapFirmware reads them only per tool.
- `M140`, `M141`, `M190` and `M191` report every heater of the slot, where RepRapFirmware reports the last.
- `M557` refuses fewer than 2 points along an axis and a third axis range.
- A refused `M557` leaves the grid as it was. RepRapFirmware keeps a grid its check rejected.
- `M915` tells every board when one refuses, as `M906` does. RepRapFirmware stops at the first.
- `M121` with no `M120` of its own file is an error and pops nothing.
- A stack overflow is the error reply of the code that caused it. RepRapFirmware reports a message and replies OK.
- `M558` and `G30` carry on when a probe's board answers with a warning, and pass it on.
- `G30 S-2` and `G30 S-3` change nothing when the probe does not trigger.
- `M569.1` always waits for standstill.
- `M569.4` sends each driver its own number, where RepRapFirmware sends the first driver's to every board.
- `M584` reports a step mode reset that a board does not answer as an error.
- An output that its board refuses is not recorded, as a refused heater or fan is not.
- A fan is created when its board answers with a warning, as a heater is.
- `M950 H Q` is limited to 1000 Hz on an existing heater too.
- A new sensor is `notReady` until it has been read, and times out after 2 s. RepRapFirmware's times out at once.
- A request from an expansion board for a firmware block past the end of the file gets one answer, `ErrBadOffset`.

### 6.2 Replies

- The wording of reports and errors is not always RepRapFirmware's. Some is standardised, and some says more, such as the range a parameter takes.
- `M115` and `M115 B0` report DSF and DuetCANMaster. `M122` and `M122 B0` include a `Duet Control Server` section.

### 6.3 Meta G-code

- A variable cannot hold an object model reference, such as `var a = move.axes`.
- A collection of objects, such as `move.axes`, has no value. A collection of scalars has.
- An expression that cannot be evaluated is an error, not null.

## 7. Input shaping

- The expansion boards shape the moves, so the reported position leads the real one while accelerating.

## 8. A pause stops the machine sooner than RepRapFirmware's does

- `M25`, and a pause caused by an event, decelerates to a stop at the earliest segment boundary it can and drops the rest of the queue. RepRapFirmware stops only where the queue already slows down enough, so it usually runs out everything queued.
- Segments already sent to the boards are not recalled.
- A job paused inside a `while` loop carries on in the iteration it stopped in, with the local variables of that point. RepRapFirmware keeps the iteration count and the variables that it had read ahead to.
- A pause from inside the job file (`M25`, `M226`, `M600` and `M601`) runs out the queue, as in RepRapFirmware.
- An event that pauses the job in a macro that cannot be restarted runs out the queue and holds the macro at its next code, where the resume carries on. RepRapFirmware drops moves of the macro that it does not make after the resume.
- `M600` takes the place of a pause that waits for a macro to end. RepRapFirmware keeps the first request and forgets the `M600`.
- One pause serves every request made before it begins, except that a deferred `M600` still pauses the job when the macro ends. RepRapFirmware pauses again for a deferred request if the job was paused in the macro meanwhile.

[Pausing and resuming a job](pause-and-resume.md) describes the mechanics.

## Further reading

- [Endstops](endstops.md): the endstop path, in the order it runs
