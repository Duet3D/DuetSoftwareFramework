# DuetCANMaster

Firmware for the main board of a Duet 3 machine driven by a single-board computer (SBC) running Duet Software Framework (DSF). It is derived from RepRapFirmware and reduced to an SPI-to-CAN-FD bridge and master step timer: DSF does the G-code interpretation and motion planning, Duet3Expansion boards do the real-time control, and this firmware connects the two.

The supported boards are the Duet 3 MB6HC and MB6XD, both SAME70.

# Building

The boards are CMake presets in the repository's root `CMakePresets.json`, built with `scripts/build.py`:

```bash
scripts/build.py DuetCANMaster_MB6HC  # one board, into build/<preset>/
scripts/build.py canmaster            # every DuetCANMaster board
```

# Documentation

[src/README.md](src/README.md) maps the source tree. The link to DSF is described in the DSF wiki's [firmware link](../Documentation/articles/firmware-link.md) and [SPI state machine](../Documentation/articles/spi-state-machine.md) articles.

# Licence
The source files in this project (RepRapFirmware) are licensed under GPLv3, see http://www.gnu.org/licenses/gpl-3.0.en.html. The associated CoreNG project, which provides a partial hardware abstraction layer, includes files derived from the Advanced Software Framework (formerly Atmel Software Framework) from Microchip. Those files have a more restrictive license, in particular they may only be used for code that targets Atmel/Microchip processors.
