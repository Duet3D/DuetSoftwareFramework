# Duet Software Framework

Welcome to the documentation of Duet Software Framework!

## Articles

These explain how DSF is put together and how data flows through it:

- [Introduction](articles/intro.md) - architecture overview and component map
- [Components](articles/components.md) - the processes and libraries that make up DSF
- [Object Model](articles/object-model.md) - the central machine-state data structure
- [Inter-process Communication](articles/ipc.md) - the DCS socket, connection modes, and commands
- [G-Code Flow](articles/gcode-flow.md) - how a G/M/T-code is parsed, processed, and executed
- [Firmware Link](articles/firmware-link.md) - the binary protocol (SPI or USB) to RepRapFirmware
- [File Management](articles/file-management.md) - the virtual SD card, jobs, macros, and file info
- [REST API](articles/rest-api.md) - the HTTP endpoints exposed by DuetWebServer
- [Plugins](articles/plugins.md) - the plugin model and permission system
- [Building from Source](articles/building.md) - prerequisites and deploying to an SBC

## API reference

Auto-generated from the source XML documentation:

1. [DuetAPI](api/DuetAPI.yml)
2. [DuetAPIClient](api/DuetAPIClient.yml)
3. [DuetControlServer](api/DuetControlServer.yml)
4. [DuetWebServer](api/DuetWebServer.yml)

For the wider Duet3D documentation, see the [Duet3D Documentation](https://docs.duet3d.com).
