# Known bugs with the existing code

## DuetControlServer

### Fractional codes
- [ ] If a user sends a fractional code, e.g. `M25.1`, the engine will treat it as the integer part, e.g. `M25`. RRF manually specifies which codes have fractional variants. And for those that don't if a fractional code is sent, RRF will look for a macro named `M25.1` and run that instead. The engine does not do that, so the fractional code is treated as the integer part.

## Duet3Expansion

### Pressure Advance Race
- [ ] ExtruderShaper::SetParameters writes five members non-atomically while the Move task may be reading them.
