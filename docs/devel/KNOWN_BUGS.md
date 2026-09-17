# Known bugs with the existing code

Tracking known issues to keep Github issues from being spammed by issues for the experimental branches. Closed issues should not be listed here.

## DuetControlServer

### Fractional codes
- [ ] Nothing maintains `job.layer`, so `JobMonitor.UpdateLayers` never records `job.layers[]` statistics and DWC's layer chart stays empty. RepRapFirmware's PrintMonitor derives the current layer from the Z height and the layer height reported by the file info.


### A deferred code stalls the moves behind it
- [ ] Found on hardware as a stutter whenever a deferred command is sent, with `Underruns [0, 96]` on the ring to go with it. A deferred code's handler runs on its channel's `ProcessInternally` stage, and the channel submits no further moves while it runs, so the ring drains. The `Movement delay` in the same report is the scheduling horizon and is constant; it is not this.

- [ ] `Code.IsCurrentlyDeferred` is set true at `PipelineBase.cs:322` and never set `false` anywhere, and `Reset()` does not clear it, so a pooled code that was deferred once carries that state into its next life along with `DeferredRing`, `DeferredAnchor` and `DeferredPredecessor`

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

## Duet3Expansion

### Pressure Advance Race
- [ ] ExtruderShaper::SetParameters writes five members non-atomically while the Move task may be reading them.
