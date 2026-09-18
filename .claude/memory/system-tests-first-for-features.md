---
name: system-tests-first-for-features
description: "Write the high-level system tests before implementing a new feature, covering inputs, object model, outgoing packets and board responses"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 1b60f10c-c8ac-4c08-9cfb-10ee9a9cf921
  modified: 2026-08-26T18:53:38.742Z
---

When a new feature is added, write its high-level system tests first, before the implementation. The tests are expected to cover:

- every user input edge case the feature can be given
- every object model change the feature is expected to make
- the outgoing DuetSbcInterface packets the feature is expected to produce, validated field by field
- every response Duet3Expansion can send back, including the failure and refusal cases
- anything else the feature makes possible that a scenario can drive

When the feature is being migrated from RepRapFirmware, the tests must capture RRF's behaviour, so that the scenario is what pins the port rather than the implementation being its own reference. See [[rrf-porting-contract]] for what "port behaviour exactly" means, and [[machine-state-in-object-model]] for what the object model is expected to hold.

**Why:** A feature written first and tested afterwards gets tests that describe what was built rather than what was asked for, and the edge cases that were never considered stay untested. Writing them first is what makes the gaps visible while they are still cheap.

**How to apply:** Start a feature by adding scenarios under `src/SystemTests/Scenarios`, driving real G-code through the hosted DuetControlServer against the scripted controller, and asserting on the object model and the captured link traffic. Only then implement, until the scenarios pass. Hardware checks ([[pi-hardware-test-workflow]]) come after, for what the bench cannot reach.
