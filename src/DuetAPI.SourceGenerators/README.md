# DuetAPI.SourceGenerators

`DuetAPI.SourceGenerators` is the Roslyn source-generator project that fills in compile-time boilerplate for [../DuetAPI/README.md](../DuetAPI/README.md). It does not ship as a runtime dependency of DSF services; it runs during compilation and emits additional C# source into the `DuetAPI` build.

## What This Project Owns

| File or directory | Purpose |
|---|---|
| [SourceGenerator.cs](SourceGenerator.cs) | Entry point registered with the Roslyn compiler. |
| [SourceGeneratorSyntaxReceiver.cs](SourceGeneratorSyntaxReceiver.cs) | Collects candidate syntax nodes during compilation. |
| [Commands/](Commands) | Generates command-related helpers and update logic. |
| [ObjectModel/](ObjectModel) | Generates object-model traversal, update, and metadata code. |
| [Descriptors.cs](Descriptors.cs) and [Helpers.cs](Helpers.cs) | Shared metadata and string-generation helpers. |

## How It Works

The generator inspects the `DuetAPI` syntax tree at compile time and emits partial classes that would be tedious and error-prone to maintain by hand. The generated code supports tasks such as:

- efficient object-model updates from JSON payloads;
- strongly typed command serialization helpers;
- metadata needed by downstream documentation and inspection code.

Because the generated code is compiled into `DuetAPI`, failures here usually surface as build errors in the API assembly rather than at runtime.

## Interfaces With Other DSF Projects

| Consumer | Interface |
|---|---|
| [../DuetAPI/README.md](../DuetAPI/README.md) | References this project as an analyzer and compiles the generated source. |
| All DSF services and tools | Consume the generated behavior indirectly through `DuetAPI`. |
| [../DocGen/README.md](../DocGen/README.md) | Benefits from the cleaner, richer API surface emitted into `DuetAPI`. |

## Relationship To RepRapFirmware

There is no direct runtime link to RepRapFirmware. The relationship is indirect: this generator helps `DuetAPI` keep its object-model and command handling code maintainable as the DSF side evolves alongside firmware contracts.

## Build And Verify

```sh
dotnet build ../DuetAPI/DuetAPI.csproj
```

That build exercises the generator because `DuetAPI` includes it as an analyzer. If you change generation logic, rebuild the full solution and run the unit tests to catch downstream breakage.

## Related Docs

- [../DuetAPI/README.md](../DuetAPI/README.md)
- [../../docs/devel/OBJECT_MODEL.md](../../docs/devel/OBJECT_MODEL.md)
