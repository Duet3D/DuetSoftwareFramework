---
name: regression-suite-workflow
description: "How to drive the DuetRegressionTesting suite against the Pi from this devcontainer, through the server on the host"
metadata: 
  node_type: memory
  type: project
  originSessionId: 5525a780-43b5-46de-8465-2c8c8b74d3c1
  modified: 2026-09-14T15:09:55.422Z
---

The regression harness runs on the **host**, not in the devcontainer, because the CAN adapter is a
host USB device. Reach it at `http://host.docker.internal:8765`. `lib/DuetRegressionTesting` is a
read-only checkout of the same tree, so testcases and references can be read from disk instead of
over the API.

- `GET /api/testcases` lists every case with its `folder`, `last_run` verdict and `known_bad` note.
- `GET /api/references/<id>` is the recorded claim; identical to `references/<id>.json` in the checkout.
- `GET /api/runs/<run_id>/detail` is the richest view: per-code reply diffs, aligned CAN traces and
  object-model deltas for one past run. This is how to find out what DSF gets wrong without touching
  the rig.
- `POST /api/job` with `{"mode":"run","profile":"dsf","tests":[...]}` starts a run; poll `GET /api/job`
  until `state` is no longer `running`. `GET /api/activity` says whether the rig is free first.

The `dsf` profile points at the Pi in [[deploy-target-pi]], so a run needs the build deployed there
first ([[pi-hardware-test-workflow]]).

**Why it matters:** the references are RepRapFirmware's recorded behaviour, which is the contract
[[rrf-porting-contract]] asks for. Reading a failing run's detail is the cheapest way to find real
deviations, and each one deserves a system test before it is fixed ([[system-tests-first-for-features]]).
