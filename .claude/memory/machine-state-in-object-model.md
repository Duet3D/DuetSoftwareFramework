---
name: machine-state-in-object-model
description: Machine state belongs in the Object Model, which must hold enough to recreate the same machine configuration; adding new fields is allowed
metadata:
  type: project
---

Information about machine state should live in the Object Model ([src/DuetAPI/ObjectModel/](src/DuetAPI/ObjectModel/)) wherever possible. The OM must contain enough information to recreate the same machine configuration. Adding new fields is explicitly allowed where the existing model does not cover something.

**Why:** The OM is the single source of truth exposed to clients and to config regeneration — state kept only in private fields or in firmware is invisible and cannot be reproduced.

**How to apply:** When implementing a feature that carries configuration or state, put it in the OM rather than in a private field, and add a new property or model class if none fits. Do not skip a value because there is no existing field for it. Note that RRF parity still applies to behaviour ([[rrf-porting-contract]]) — extending the OM to expose state is not a functional deviation.
