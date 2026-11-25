# Known bugs with the existing code

Tracking known issues to keep Github issues from being spammed by issues for the experimental branches. Closed issues should not be listed here.

## DuetControlServer

### Fractional codes
- [ ] Nothing maintains `job.layer`, so `JobMonitor.UpdateLayers` never records `job.layers[]` statistics and DWC's layer chart stays empty. RepRapFirmware's PrintMonitor derives the current layer from the Z height and the layer height reported by the file info.


### A deferred code stalls the moves behind it
- [ ] Found on hardware as a stutter whenever a deferred command is sent, with `Underruns [0, 96]` on the ring to go with it. A deferred code's handler runs on its channel's `ProcessInternally` stage, and the channel submits no further moves while it runs, so the ring drains. The `Movement delay` in the same report is the scheduling horizon and is constant; it is not this.

- [ ] `Code.IsCurrentlyDeferred` is set true at `PipelineBase.cs:322` and never set `false` anywhere, and `Reset()` does not clear it, so a pooled code that was deferred once carries that state into its next life along with `DeferredRing`, `DeferredAnchor` and `DeferredPredecessor`

### Object Model
- [ ] `om-default-boards` records `1`; DSF reports `true`.

### Kinematics
- [ ] Delta kinematics can't set `NumTowers` using gcode
- [ ] A hangprinter takes no `M669` parameters beyond `K`, and `M666` is refused: the spool buildup and flex compensation, and the steps per mm RepRapFirmware works out from them, are not ported. `M669Tests.M669HangprinterSetsTheLineMotorsStepsPerMm`.

### Babystepping
- [ ] `M290` takes effect on the next move built; RepRapFirmware also pushes it into the moves already queued (`DDARing::PushBabyStepping`).

### Expansion boards that reset
- [ ] Nothing re-creates the input monitors of a board that has reset, and nothing fails if `expansion-reconnect.g` does not. RepRapFirmware's homing fails loudly, because it asks the board to prime each switch.

### Probing and the move transforms
- [ ] `G30` without P first moves Z to the dive height above the axis minimum (`ProbeStartHeight`) and dives from there; RepRapFirmware dives from where the head is (`ExecuteG30` goes straight to `probingAtPoint2a`). On the regression bench, where Z's minimum is −100, that is a 99 mm move down before the dive. KnownGap scenario `GeneratedMoveTransformTests.G30WithoutPDivesFromWhereTheHeadIs`.
- [ ] `G29`'s travel height and dive target are measured from Z's minimum (`ZMin + dive + trigger`, `ZMin − dive + trigger`); RepRapFirmware's grid probing measures both from Z0 (`gridProbing1`, `gridProbing3`). The two agree only when `M208` puts Z's minimum at 0.
- [ ] The lift after a tap goes to the second dive height above where the probe stopped (`stopped + DiveHeights[1]`, in `TapAsync`, for `G30` and `G29`), and there is no lift after the tap whose reading agrees with the one before. RepRapFirmware lifts after every tap, to `ZProbe::GetStartingHeight(acceptReading, g30zHeightError)` (`probingAtPoint4a`, `gridProbing4a`), measured from Z0: the first dive height plus the trigger height once the reading is accepted, and before another tap the second dive height plus the last height error plus the trigger height if the second dive height is the lower, otherwise the first.
- [ ] Loading a height map does not bring the interpreter into step, so Z keeps reading the uncorrected height; RepRapFirmware's `LoadHeightMap` ends with `ActivateHeightmap` (`GCodes.cpp:5576-5588`), which re-reads the position and brings the user position into step with `ToolOffsetInverseTransform`, and `g30-dive-with-mesh` reads Z −0.07 before its dive. KnownGap scenario `GeneratedMoveTransformTests.LoadingAHeightMapReportsZWithTheCorrectionTakenOff`.
- [ ] The generated moves keep the `RawMove` flags each site has always set, which differ from what RepRapFirmware's `MovementState::SetDefaults` (from `SetMoveBufferDefaults`) gives a special move: it clears `isCoordinated`, `usingStandardFeedrate`, `applyM220M221` and `usePressureAdvance`, where DSF's probing moves keep `IsCoordinated` and `UsingStandardFeedrate` at their default of true and the move back on resume sets `IsCoordinated`. `ApplyM220M221` and `UsePressureAdvance` default to false, as RepRapFirmware's do. None of them changes the packets of these moves in the scenarios that cover them.
- [ ] After a probing move is stopped, the engine's commanded motor positions (`DuetRT_MotionGetMotorPositions`, which `MovePlanner.ResyncFromEngine` reads) can put a drive the stopped move did not turn one microstep from where the move before left it: X reads 1599 after a travel to 1600, and 1601 after a travel back down to 1600. The builder is resynced to what is read, so the next move is planned a step off: if the read is wrong the motor is moved a step it should not be, and the scenario cannot tell that from the read being right and the next move making up a step the motor missed. The resync path is the one every stopped move takes, not one specific to probing. A likely cause, not yet confirmed, is `MotionSystem::GetMotorPositions` (`src/DuetRealtimeCore/src/Motion/MotionSystem.cpp`) reporting each drive's tracker position as of its last retired segment. Seen in `GeneratedMoveTransformTests.GridProbingWithADuplicatingToolLeavesTheToolsOtherCarriageAlone`, which allows X that step.
- [ ] `G30` does not refuse with "Insufficient axes homed for bed probing" when the kinematics' `AxesToHomeBeforeProbing` are not homed (`GCodes2.cpp` case 30). `G30Tests.G30RefusesUntilXAndYAreHomed`.
- [ ] `G30` with Z not homed moves to the start height and dives to the homed target; RepRapFirmware dives from where Z is to −1.1 × Z's length (`probingAtPoint3`). `G30Tests.G30WithZNotHomedDivesAsAHomingMove`.
- [ ] A plain `G30` whose probe never triggers does not lift back to `GetStartingHeight` as RepRapFirmware does. `G30Tests.G30LiftsBackToTheStartHeightWhenTheProbeNeverTriggers`.
- [ ] `G30 S-1`, `S-2` and `S-3` tap up to `M558 A` times and lift after each; RepRapFirmware taps once and leaves the head where it stopped (`probingAtPoint4` goes to 7). `G30Tests.TheSParametersTapOnceAndLeaveTheHeadWhereItStopped`.
- [ ] `G30 S-2` is not implemented: it should set the selected tool's Z offset to −(stopped − trigger height), set Z in `offsetsProbed` and update the user position, and leave both alone when the probe never triggers. `G30Tests.G30S2SetsTheSelectedToolsZOffset`, `G30S2WithTheProbeNeverTriggeringLeavesTheToolOffset`.
- [ ] `G30 P` is not implemented in any form: probing at a point with the probe offset, recording a given Z, the "G32 bed probe heights" report and the `S>0` calibration errors. `G30Tests.G30PProbesAtThePointAndS1ReportsTheHeights`, `G30PWithAHeightRecordsItWithoutProbing`, `G30PWithACalibrationTheMachineCannotDoIsAnError`.
- [ ] After a board refuses `SetProbing(true)`, `G30` still sends it a second `ChangeInputMonitor` to restore the interval; RepRapFirmware sends nothing more. `G30Tests.ABoardThatRefusesToWatchTheProbeIsToldNothingMore`.
- [ ] `G30` runs only `deployprobe<K>.g` / `retractprobe<K>.g` with no fallback to `deployprobe.g` / `retractprobe.g`, runs them when the probe was deployed by `M401`, and redeploys every probe type before each tap; RepRapFirmware deploys and retracts a BLTouch around every tap and other types once. `G30Tests.G30RunsTheUnnumberedProbeMacros`, `G30LeavesAProbeDeployedByM401Alone`, `TheProbeIsDeployedAndRetractedAroundTheTapsAsItsTypeNeeds`. `GeneratedMoveTransformTests.AMacroBetweenTapsStartsFromWhereTheLiftLeftTheHead` relies on the per-tap deploy and has to change with the fix.
- [ ] A second `G30` tap measures from the original datum; in RepRapFirmware it measures from the datum the first tap set, so `H`, which the first tap ignores, moves the datum through the later taps. `G30Tests.G30TapsFromTheLiftAndMeasuresTheSecondTapFromTheNewDatum`, `G30HMovesTheDatumThroughTheLaterTaps`.
- [ ] `G30` taps that never agree within `M558 S` are averaged without "Z probe readings not consistent". `G30Tests.G30WithReadingsThatNeverAgreeUsesTheirMeanAndSaysSo`.
- [ ] With `M558 F` fast:slow, the fast tap counts as one of the `A` taps; RepRapFirmware makes an uncounted fast tap and then counted slow taps (`tapsDone` starts at −1). `G30Tests.FastThenSlowProbingTapsFastFirstAndCountsTheSlowTap`.
- [ ] `G30` with a manual probe (`M558 P0`) is refused; RepRapFirmware opens a "Manual bed probing" message box and uses the height confirmed with `M292`. `G30Tests.G30WithAManualProbeAsksTheUserForTheHeight`.

### Probe configuration (M558, G31)
- [ ] `sensors.probes[].speeds` is held in mm/s where RepRapFirmware reports mm/min, as DSF does for `travelSpeed` and `move.axes[].speed`; it should have 3 entries for type 11 (the third defaulting to the first) and 2 otherwise. `M558Tests.M558SetsProbingSpeeds`.
- [ ] A new probe has two offsets and `calibrationTemperature` 0; RepRapFirmware gives one offset per axis with offsets[Z] = −0.7, and 25 °C. `M558Tests.M558NewProbeHasOneOffsetPerAxis`, `M558NewProbeIsCalibratedAt25C`, `M851Tests`.
- [ ] `M558 P` on an existing probe keeps its old `M558` and `G31` settings; RepRapFirmware recreates it with defaults. `M558Tests.M558RedefinitionResetsTheProbe`.
- [ ] A refused `M558 P`/`C` (missing pin, `P2`, `P5`, an output port, a board refusing the input monitor) keeps the old probe, or a probe naming a pin nothing watches; RepRapFirmware has already deleted the old probe and leaves `sensors.probes[K]` null. `M558Tests.M558TypeWithoutAPortIsRefusedAndDeletesTheProbe`, `M558ExpansionBoardRefusalsDeleteTheProbe`, `M558BoardRefusalLeavesNoProbe`.
- [ ] The `M558` report does not ask the probe's board for its pin description (`ChangeInputMonitor` action 5) or fail when the board cannot give it. `M558Tests.M558ReportQuotesTheBoardsPinDescription`, `M558ReportFailsWhenTheBoardCannotNameThePin`.
- [ ] A scanning probe (`M558 P11`) takes no initial reading (`ReadInputsRequest`) and leaves `isCalibrated`, `scanCoefficients` and `touchMode` null. `M558Tests.M558ScanningProbeTakesAnInitialReading`.
- [ ] `M558 A0` is stored as 1; RepRapFirmware only limits A from above. `M558Tests.M558StoresZeroMaxTapsAsGiven`.
- [ ] `M558 B1` does not suspend the heaters (`SetHeaterTemperature` commands 4 and 5) around the probing move. `M558Tests.M558B1SuspendsHeatersAroundTheProbingMove`.
- [ ] The load cell probe (`M558 P12`, with `V` and `U`) is not supported, and `V`/`U` on another probe type are taken as a report instead of refused. `loadCell.gramsPerCount` and `preloadWindow` have no home in the object model yet. `M558Tests.M558CreatesALoadCellProbe`, `M558RefusesLoadCellParametersForOtherProbes`.
- [ ] `G31` temperature compensation: `T` without `S` does not calibrate at the sensor's current temperature, a sensor with no reading is not waited for and warned about, an unknown sensor `H` is not warned about and its coefficients are kept, and `S` without `T` changes `calibrationTemperature`. `G31Tests.G31CoefficientWithoutSCalibratesAtTheCurrentTemperature`, `G31CoefficientAgainstASilentSensorUsesTheDefault`, `G31CoefficientForAnUnknownSensorIsIgnored`, `G31CalibrationTemperatureNeedsACoefficient`.

### Endstops (M574)
- [ ] When a board refuses an `M574` pin, or a later switch of a multi-pin endstop, the axis keeps an endstop naming the refused pin, and a handle never created is deleted; RepRapFirmware has already deleted the old endstop and leaves the axis with none. `M574Tests.BoardRefusingThePinLeavesTheAxisWithoutAnEndstop`, `RefusedReconfigurationRemovesTheOldEndstop`, `SecondSwitchRefusedGivesBackTheFirst`.
- [ ] `M574 S1` without P on an axis with no switch creates an endstop with no port instead of "Pin name needed for switch-type endstop", and `M574 X2` on an existing switch recreates its monitor instead of only changing the end. `M574Tests.SwitchWithoutAPinNeedsOneAlreadyConfigured`, `PositionWithoutAPinMovesAnExistingSwitch`.
- [ ] A switch replaced by a stall or Z-probe endstop keeps its `port`. `M574Tests.ReplacedSwitchTakesItsPortWithIt`.
- [ ] An S2 endstop's `triggered` does not follow its probe (`ZProbeEndstop::Stopped`). `M574Tests.ZProbeEndstopReadsTriggeredFromItsProbe`.
- [ ] `M574 S0` is accepted as type `unknown` instead of refused as no longer supported, and the report names an S5 endstop "unknown" instead of "motor stall (any motor)". `M574Tests.TypeZeroIsRefused`, `ReportDescribesAnEncoderEndstopAsAStall`.
- [ ] `M574 E` cannot bind an input until `M950 J` is ported, and no move stops on an extruder's bound input yet. `M574Tests.FilamentEndstopBindsAnInputToAnExtruder`.

### Probe grid and bed compensation (M557, M561)
- [ ] `M557` caps the grid at 441 points; RepRapFirmware on the MB6HC allows 961. `M557Tests.M557AcceptsTheMb6hcGridPointLimit`.
- [ ] `M557` accepts a radius between 0 and 1, which should be refused. `M557Tests.M557RefusesARadiusUnderOne`.
- [ ] `move.compensation.probeGrid.radius` starts at 0; RepRapFirmware's `GridDefinition()` starts it at −1. `M557Tests.ProbeGridStartsUndefined`.
- [ ] `M561` does not recompute the user position (`GCodes::ClearBedMapping`), so Z keeps reading the corrected height after the map is cleared. `M561Tests.M561KeepsTheHeadWhereItIs`, `M561RemovesTheMapFromTheUserPosition`.

### Stall detection (M915)
- [ ] `M915 P` takes a single driver; RepRapFirmware takes a list across boards. `M915Tests.M915PTakesADriverListAcrossBoards`.
- [ ] `M915` forwards or clamps S outside −128..127, T ≥ 65536 and R ≥ 4 instead of refusing them before anything is sent. `M915Tests.M915RefusesAValueOutOfRange`.
- [ ] `M915` naming no driver is an error; RepRapFirmware replies OK and sends nothing. `M915Tests.M915NamingNoDriverIsNotAnError`.
- [ ] `M915` records its settings for drivers whose board refused them, and adds a `boards[].drivers[]` entry for a driver past the end of the board. `M915Tests.M915DoesNotRecordWhatABoardRefused`, `M915OnADriverPastTheEndOfABoardIsIgnoredByTheBoard`.

## DuetWebServer

### The HTTP API stayed dead after a deploy, once
- [ ] After `scripts/deploy.py -H <pi> dsf --build --start-services`: DuetControlServer came up and ran config.g normally, `systemctl is-active duetwebserver` said active, and every request to it was refused at the socket. `systemctl restart duetwebserver` fixed it. The cause is not known.

## DuetCANMaster

### A stuck CAN transmit buffer stalls the SPI link
- [ ] `CanInterface::SendCanRequest` sends inline, and `CanDevice::SendMessage` blocks in
  `IsSpaceAvailable` for up to `UsualSendTimeout` (200ms) when the transmit buffer is still occupied.
  It runs on the SBC task, which is the lowest priority of the lot and is also the SPI exchange loop,
  so a bus where nothing acknowledges surfaces on the DCS side as link timeouts rather than as CAN
  errors. RepRapFirmware blocks for the same 200ms but on Main, which carries no link.

  Left as it is for now. The fix, if it is wanted, is to hand SBC-originated requests to the existing
  `CanSenderTask` rather than sending them inline: the outcome is already reported asynchronously
  through `ReportCanMessageSent`, so nothing else would have to move.

## RepRapFirmware (upstream)

### An MB6HC in expansion mode never re-announces itself, so it disappears from `boards[]`
- [ ] `CanInterface::mainBoardAcknowledgedAnnounce` is a one-way latch in RepRapFirmware: set once by
  `MainBoardAcknowledgedAnnounce()` and never cleared. Duet3Expansion clears it in
  `UpdateSyncLockState` when time sync is regained after a loss, which is what makes a board announce
  again after the main board restarts. RepRapFirmware in expansion mode has no equivalent at all - no
  `syncLockLost`, no sync-loss timeout, no heater shutdown - so once acknowledged it is silent for the
  rest of its uptime. Restart DuetCANMaster and that board's `boards[]` entry has a CAN address and
  live readings and no identity until it is reset.

  Nothing on this side can recover it: the board will not speak, DuetCANMaster has nothing to replay,
  and the protocol has no way to ask. To be reported upstream; `M999 B<n>` is the workaround.

## Duet3Expansion

### Pressure Advance Race
- [ ] ExtruderShaper::SetParameters writes five members non-atomically while the Move task may be reading them.
