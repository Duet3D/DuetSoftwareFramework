---
name: dsf-architecture-migration
description: Architecture the project is migrating to — mandatory SBC running DSF, Duet3Expansion everywhere else, DuetCANMaster as SPI-to-CAN-FD bridge
metadata:
  type: project
---

Old architecture: RepRapFirmware ran on a mainboard with an *optional* SBC running DuetSoftwareFramework. That duplicated a lot of functionality — networking, file management, GCode processing between DSF and RRF; real-time control between RRF and Duet3Expansion.

New architecture: DSF runs on a *mandatory* SBC and everything else runs [src/Duet3Expansion/](src/Duet3Expansion/). A middle-layer firmware, [src/DuetCANMaster/](src/DuetCANMaster/), runs on a Raspberry Pi hat-equivalent board and acts as the SPI to CAN-FD converter plus the master step timer.

**Why:** The migration is the reason [[rrf-reference-clone]] exists and the reason work spans DSF, Duet3Expansion, and DuetCANMaster together.

**How to apply:** When placing new functionality, put host-side concerns (networking, files, GCode) in DSF, motion/real-time control in Duet3Expansion, and only SPI↔CAN-FD bridging and step timing in DuetCANMaster. Don't reintroduce the DSF/RRF duplication.
