# Known bugs with the existing code

## DuetControlServer

### Fractional codes
- [x] If a user sends a fractional code, e.g. `M25.1`, the engine will treat it as the integer part, e.g. `M25`. Fixed by the code class table (MOTION_SYNCHRONISED_ACTIONS.md §5.1): a fraction without a table row runs the macro named after it if one exists and resolves as unsupported otherwise.
- [ ] Nothing maintains `job.layer`, so `JobMonitor.UpdateLayers` never records `job.layers[]` statistics and DWC's layer chart stays empty. RepRapFirmware's PrintMonitor derives the current layer from the Z height and the layer height reported by the file info.

### Read-ahead moves run after a feedhold purge
- [x] A pause's feedhold purges the queued moves and cancels the job's read-ahead, but the read-ahead codes ran on anyway and their moves executed while the machine was "pausing". The cancellation was aimed at a token nothing observed: the job file loop assigned its token to each code's `CancellationToken` property, and `Code.ExecuteAsync()` then overwrote that property with the channel token, so `StopReadingForPause` cancelled a token no job code held. Fixed by passing the job token into `ExecuteAsync` instead; the same defect was what let a pause hang behind a blocking `M116` (the `SystemTests` scenario `PauseInterruptsTemperatureWait` covers that case, and `PauseAndResumeMidJob` the moves).

### A job ended before the moves it had queued were made
- [x] A movement code finishes when its move is queued, so `DoFilePrint` reached the end of a job file seconds before the machine reached the end of the job: `stop.g` ran, `state.status` fell to `idle`, and an `M25` from a console was refused with "Cannot pause print, because no file is being printed!" while the head was still moving. RepRapFirmware does not close the file until every move has completed, for both of those reasons (GCodes.cpp:706). `DoFilePrint` now waits for standstill when the file runs out of codes, unless the job is already pausing, cancelling or aborting, where the pause sequence does its own waiting.

### Waiting for a full object model update never returned
- [x] `ObjectModel.WaitForFullUpdateAsync` waited on an event nothing raised: `FullyUpdated`/`FullyUpdatedAsync` had no callers, because a finished full model update is what the SPI model updater used to signal and DuetControlServer now owns the object model outright. Everything that awaited it hung for ever, `M26` and `M27` among them. The infrastructure is gone - the condition variable, its lock and the four methods - and with it the two waits in `M26` and `M27`, whose `inputs[].motionSystem` is a local field nothing writes either, and the one before the plugins start. The simulated-time loop in `JobProcessor` now waits on `WaitForUpdateAsync`, which every write of the object model raises, and the `SyncObjectModel` command completes at once because a caller holding the read lock already sees every effect that has happened.

## DuetSbcInterface

### The permanent motion arena was never given back
- [x] `MotionSystem::Init` reserved the 4 MB permanent arena and nothing ever released it, so the DDA ring each motion system allocated stayed allocated after the instance was destroyed. One instance per process is all DuetControlServer creates, so a machine never noticed; the `SystemTests` bench, which hosts DCS in-process once per scenario, ran out after about thirty scenarios and `MemoryArena::Allocate` aborted the whole test process. `Reserve`/`Release` now count their users - the bench compares one machine against another with two motion systems alive at once - the region is unmapped when the last of them goes, and `MotionSystem`'s destructor is what lets go. `MoveSegment`'s free list is reset at the same time: it is one static per process while the arena is not, so without that the next motion system would be handed recycled segments pointing into memory that had been unmapped.

## Duet3Expansion

### Pressure Advance Race
- [ ] ExtruderShaper::SetParameters writes five members non-atomically while the Move task may be reading them.
