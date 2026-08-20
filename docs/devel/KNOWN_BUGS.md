# Known bugs with the existing code

## DuetControlServer

### Fractional codes
- [x] If a user sends a fractional code, e.g. `M25.1`, the engine will treat it as the integer part, e.g. `M25`. Fixed by the code class table (MOTION_SYNCHRONISED_ACTIONS.md §5.1): a fraction without a table row runs the macro named after it if one exists and resolves as unsupported otherwise.
- [ ] Nothing maintains `job.layer`, so `JobMonitor.UpdateLayers` never records `job.layers[]` statistics and DWC's layer chart stays empty. RepRapFirmware's PrintMonitor derives the current layer from the Z height and the layer height reported by the file info.

## Duet3Expansion

### Pressure Advance Race
- [ ] ExtruderShaper::SetParameters writes five members non-atomically while the Move task may be reading them.
