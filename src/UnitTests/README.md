# UnitTests

`UnitTests` is the NUnit test project for DSF. It provides regression coverage for the API contracts, file parsing, IPC-facing helpers, object-model filtering and observation, SPI packet handling, and the standalone HTTP client.

## What This Project Covers

| Area | Examples |
|---|---|
| [Commands/](Commands) | G/M/T-code parsing and command behavior. |
| [File/](File) | File-path handling, G-code metadata parsing, config parsing, and position extraction. |
| [IPC/](IPC) | Subscription and IPC-related behavior. |
| [Machine/](Machine) | Object-model expressions, filters, and observer behavior. |
| [SPI/](SPI) | Packet reader and writer logic used by the firmware link. |
| [HttpClient/](HttpClient) | Remote HTTP session behavior from `DuetHttpClient`. |
| [Utility/](Utility) | Support logic such as height-map parsing. |

The project also carries sample inputs such as `heightmap.csv` and representative G-code resources.

## How It Fits Into The Solution

`UnitTests` references:

- [../DuetAPI/README.md](../DuetAPI/README.md)
- [../DuetControlServer/README.md](../DuetControlServer/README.md)
- [../DuetHttpClient/README.md](../DuetHttpClient/README.md)

That makes it the main safety net for changes to public contracts, the DCS protocol implementation, and HTTP client behavior.

## Relationship To RepRapFirmware

The tests do not speak to live firmware, but many of them validate assumptions that have to remain compatible with RepRapFirmware, especially around packet formats, file-path handling, and object-model data.

## Build And Verify

```sh
dotnet test UnitTests.csproj
```

Use targeted filters when iterating on a specific subsystem, for example:

```sh
dotnet test UnitTests.csproj --filter SPI
```

## Related Docs

- [../DuetControlServer/README.md](../DuetControlServer/README.md)
- [../DuetHttpClient/README.md](../DuetHttpClient/README.md)
- [../../docs/devel/SPI_LINK.md](../../docs/devel/SPI_LINK.md)
- [../../docs/devel/OBJECT_MODEL.md](../../docs/devel/OBJECT_MODEL.md)
