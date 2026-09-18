---
name: pi-hardware-test-workflow
description: How to deploy to the test Pi and reproduce G-code issues there with CodeConsole and journalctl
metadata: 
  node_type: memory
  type: project
  originSessionId: c8e2e1d7-cf85-4bfd-9092-431fa312c65c
  modified: 2026-08-21T17:05:58.303Z
---

To test on real hardware, deploy with `./scripts/build.sh --all --target <pi-ip> --start-services`
(SSH user `root`; the address is in [[deploy-target-pi]]). On the Pi:

- Send codes with `/opt/dsf/bin/CodeConsole` (pipe codes into stdin, e.g. `M32 "0:/gcodes/file.gcode"`
  to start a job, `M409 K"move.axes[0].machinePosition"` to query the object model).
- The virtual SD is `/opt/dsf/sd/`, so `0:/gcodes/...` maps to `/opt/dsf/sd/gcodes/...`.
- DuetControlServer already logs at debug level to the journal: `journalctl -u duetcontrolserver --since <time>`
  shows per-code `Starting`/`Processing`/`Finished` lines and CAN traffic (`SetFanSpeed`,
  move messages), which is enough to see whether a code produced hardware effects and when.
- The debug line `Failed to flush file codes on stage Start for <job>` right before
  `Finished job file` is the best-effort EOF flush in JobProcessor returning false after the
  file's stack level is gone; it appears on successful jobs too and is not an error.
