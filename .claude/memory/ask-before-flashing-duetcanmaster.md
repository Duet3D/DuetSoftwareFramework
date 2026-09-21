---
name: ask-before-flashing-duetcanmaster
description: Never flash DuetCANMaster; ask the user to do it and wait
metadata: 
  node_type: memory
  type: feedback
  originSessionId: c9890fdd-4d89-41fe-9162-2e1ac69bbf66
  modified: 2026-09-21T09:34:40.887Z
---

Do not flash the DuetCANMaster firmware, by `M997`, bossac, OpenOCD or any other route. When a change
needs new controller firmware on the test rig, build it, say so, and ask the user to flash it.

**Why:** The infrastructure to flash DuetCANMaster using DSF has not been implemented yet.

**How to apply:** Stop and ask when you want to flash DuetCANMaster