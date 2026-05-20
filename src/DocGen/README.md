# DocGen

`DocGen` is the object-model documentation generator used by the DSF documentation pipeline. It reflects over `DuetAPI`, reads the XML comments emitted by that project, and produces a markdown document describing the machine model.

## How It Works

[Program.cs](Program.cs) does three main things:

1. loads `../DuetAPI/DuetAPI.xml` so it can read API comments;
2. reflects over `DuetAPI.ObjectModel.ObjectModel` and recursively walks the model tree;
3. writes `documentation.md` by combining [header.md](header.md), generated content, and [footer.md](footer.md).

[XMLHelper.cs](XMLHelper.cs) resolves XML documentation for properties and enum values so the generated output stays tied to the source comments instead of hand-maintained copies.

## Interfaces

| Interface | Details |
|---|---|
| [../DuetAPI/README.md](../DuetAPI/README.md) | Supplies the reflected model types and XML comments. |
| [../Documentation/README.md](../Documentation/README.md) | Consumes the generated markdown as part of the wider docs workflow. |
| RepRapFirmware | No direct interface. The generated model docs still describe many firmware-originated fields because DCS mirrors them from RRF. |

## Build And Verify

Build `DuetAPI` first so `DuetAPI.xml` exists, then run `DocGen`:

```sh
dotnet build ../DuetAPI/DuetAPI.csproj -c Release
dotnet run --project DocGen.csproj
```

Review the generated `documentation.md` before publishing documentation changes.

## Related Docs

- [../Documentation/README.md](../Documentation/README.md)
- [../../docs/devel/OBJECT_MODEL.md](../../docs/devel/OBJECT_MODEL.md)
