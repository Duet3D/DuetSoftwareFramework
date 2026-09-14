# Known bugs with the existing code

Tracking known issues to keep Github issues from being spammed by issues for the experimental branches. Closed issues should not be listed here.

## DuetControlServer

### Fractional codes
- [ ] Nothing maintains `job.layer`, so `JobMonitor.UpdateLayers` never records `job.layers[]` statistics and DWC's layer chart stays empty. RepRapFirmware's PrintMonitor derives the current layer from the Z height and the layer height reported by the file info.


### A deferred code stalls the moves behind it
- [ ] Found on hardware as a stutter whenever a deferred command is sent, with `Underruns [0, 96]` on the ring to go with it. A deferred code's handler runs on its channel's `ProcessInternally` stage, and the channel submits no further moves while it runs, so the ring drains. The `Movement delay` in the same report is the scheduling horizon and is constant; it is not this.

## Duet3Expansion

### Pressure Advance Race
- [ ] ExtruderShaper::SetParameters writes five members non-atomically while the Move task may be reading them.
