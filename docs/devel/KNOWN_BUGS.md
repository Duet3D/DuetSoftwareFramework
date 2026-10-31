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

### Probing and the move transforms
- [ ] `G30` without P first moves Z to the dive height above the axis minimum (`ProbeStartHeight`) and dives from there; RepRapFirmware dives from where the head is (`ExecuteG30` goes straight to `probingAtPoint2a`). On the regression bench, where Z's minimum is −100, that is a 99 mm move down before the dive. KnownGap scenario `GeneratedMoveTransformTests.G30WithoutPDivesFromWhereTheHeadIs`.
- [ ] `G29`'s travel height and dive target are measured from Z's minimum (`ZMin + dive + trigger`, `ZMin − dive + trigger`); RepRapFirmware's grid probing measures both from Z0 (`gridProbing1`, `gridProbing3`). The two agree only when `M208` puts Z's minimum at 0.
- [ ] The lift after a tap goes to the second dive height above where the probe stopped (`stopped + DiveHeights[1]`, in `TapAsync`, for `G30` and `G29`), and there is no lift after the tap whose reading agrees with the one before. RepRapFirmware lifts after every tap, to `ZProbe::GetStartingHeight(acceptReading, g30zHeightError)` (`probingAtPoint4a`, `gridProbing4a`), measured from Z0: the first dive height plus the trigger height once the reading is accepted, and before another tap the second dive height plus the last height error plus the trigger height if the second dive height is the lower, otherwise the first.
- [ ] Loading a height map does not bring the interpreter into step, so Z keeps reading the uncorrected height; RepRapFirmware's `LoadHeightMap` ends with `ActivateHeightmap` (`GCodes.cpp:5576-5588`), which re-reads the position and brings the user position into step with `ToolOffsetInverseTransform`, and `g30-dive-with-mesh` reads Z −0.07 before its dive. KnownGap scenario `GeneratedMoveTransformTests.LoadingAHeightMapReportsZWithTheCorrectionTakenOff`.
- [ ] After a plain `G30`, `ZeroTheHeightMapHere` skews `Builder.StartCoordinates`, which already carry the skew, and with the selected tool's axis map; RepRapFirmware's `SetZeroHeightError` skews `raw.coords`, before the skew, with no tool. With skew and a height map both set the map is zeroed at the wrong point.
- [ ] `BuildRawMove` seeds a move from the tool transform alone, so an axis the selected tool maps away from (X on a tool mapping X onto U) is commanded to 0 by the next `G1` instead of staying where it is; RepRapFirmware leaves that slot of `raw.coords` as the last move left it.
- [ ] The move back on resume sets the interpreter's position before queueing and does not put it back when the ring is full, so the retry finds nothing left to restore and the head is not moved back.
- [ ] The generated moves keep the `RawMove` flags each site has always set, which differ from what RepRapFirmware's `MovementState::SetDefaults` (from `SetMoveBufferDefaults`) gives a special move: it clears `isCoordinated`, `usingStandardFeedrate`, `applyM220M221` and `usePressureAdvance`, where DSF's probing moves keep `IsCoordinated` and `UsingStandardFeedrate` at their default of true and the move back on resume sets `IsCoordinated`. `ApplyM220M221` and `UsePressureAdvance` default to false, as RepRapFirmware's do. None of them changes the packets of these moves in the scenarios that cover them.
- [ ] After a probing move is stopped, the engine's commanded motor positions (`DuetRT_MotionGetMotorPositions`, which `MovePlanner.ResyncFromEngine` reads) can put a drive the stopped move did not turn one microstep from where the move before left it: X reads 1599 after a travel to 1600, and 1601 after a travel back down to 1600. The builder is resynced to what is read, so the next move is planned a step off: if the read is wrong the motor is moved a step it should not be, and the scenario cannot tell that from the read being right and the next move making up a step the motor missed. The resync path is the one every stopped move takes, not one specific to probing. A likely cause, not yet confirmed, is `MotionSystem::GetMotorPositions` (`src/DuetRealtimeCore/src/Motion/MotionSystem.cpp`) reporting each drive's tracker position as of its last retired segment. Seen in `GeneratedMoveTransformTests.GridProbingWithADuplicatingToolLeavesTheToolsOtherCarriageAlone`, which allows X that step.

## DuetWebServer

### The HTTP API stayed dead after a deploy, once
- [ ] After `scripts/build.sh --all --target <pi> --start-services`: DuetControlServer came up and ran config.g normally, `systemctl is-active duetwebserver` said active, and every request to it was refused at the socket. `systemctl restart duetwebserver` fixed it. The cause is not known.

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
