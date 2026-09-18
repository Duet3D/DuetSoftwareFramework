---
name: machine-state-in-object-model
description: Machine state belongs in the Object Model, which must hold enough to recreate the same machine configuration; adding new fields is allowed
metadata:
  type: project
---

Information about machine state should live in the Object Model ([src/DuetAPI/ObjectModel/](src/DuetAPI/ObjectModel/)). The OM must contain enough information to recreate the same machine configuration. Adding new fields is explicitly allowed where the existing model does not cover something.

Every configuration G/M-code ported into DuetControlServer has to satisfy this. Forwarding a setting to an expansion board over CAN without recording it is not acceptable, even when the board is the thing that acts on it. Transient state (driver enable/disable, current position) is exempt; configuration is not.

**Why:** The OM is the machine's definition and the single source of truth exposed to clients, not a cache of what the hardware happens to be doing. If it cannot recreate the machine then M500 cannot write config-override.g, the web interface cannot show the configuration, and a board that reconnects cannot be reconfigured from this side. The user raised this after reviewing the M569 work, where the driver configuration was sent over CAN and nothing was stored, on the reasoning that "the driver's configuration is the board's". That reasoning was wrong.

**How to apply:** When porting a configuration code or implementing a feature that carries configuration, ask what would be lost if the process restarted and had to rebuild the machine from `model` alone. Anything that would be lost belongs in the OM; add a new property or model class if none fits, rather than a private field. Note that RRF parity still applies to behaviour ([[rrf-porting-contract]]); extending the OM to expose state is not a functional deviation.
