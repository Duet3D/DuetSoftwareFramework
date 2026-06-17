# Building from source

DSF is a .NET solution. The application projects target the current .NET runtime (`net10.0`); the
shared libraries multi-target older frameworks as well so they can be consumed by external tools. This
article covers building DSF and deploying a modified build to an SBC for testing.

## Prerequisites

- The [.NET SDK](https://dotnet.microsoft.com/download) for the framework the projects target
  (currently .NET 10).
- A checkout of this repository.
- For cross-building and deploying to an SBC: SSH access to the target.

## Building

From the repository root, build a project for the SBC's architecture with a self-contained runtime:

```sh
dotnet build src/DuetControlServer/DuetControlServer.csproj -r linux-arm64 --self-contained
```

Use `-r linux-arm` for 32-bit targets. To build directly on the SBC into the standard install
location:

```sh
dotnet build -o /opt/dsf/bin
```

### The VERIFY_OBJECT_MODEL constant

Development builds may define the `VERIFY_OBJECT_MODEL` constant
(`-p:DefineConstants=VERIFY_OBJECT_MODEL`). It enables extra consistency checks in the generated
[object-model](object-model.md#json-serialization) update code and is deliberately left out of release
packages, where it would only add overhead.

## Deploying to an SBC for testing

The typical loop is: build for the target architecture, stop the DSF services, copy the binaries over,
and restart the services.

```sh
# stop the services
ssh root@<sbc> "systemctl stop duetcontrolserver duetwebserver"

# sync the build output (example for DuetControlServer)
rsync -a --delete src/DuetControlServer/bin/Debug/net10.0/linux-arm64/ root@<sbc>:/opt/dsf/bin/

# restart
ssh root@<sbc> "systemctl start duetcontrolserver duetwebserver"
```

Deploying over SSH as root requires root SSH login to be enabled on the SBC (set a root password and
permit root login in the SSH daemon configuration). On Windows, `rsync` and `libxxhash` from MSYS2 can
be dropped into the Git-for-Windows `usr` tree to make the same workflow available there.

> The exact service names, paths, and any packaging steps are environment-specific. The repository
> `Makefile` and `pkg/` directory hold the canonical packaging recipes used to build the `.deb` /
> `.rpm` / Arch packages.

## Building the documentation

The documentation lives under `src/Documentation/` and is built with
[DocFX](https://dotnet.github.io/docfx/). The hand-written articles plus an API reference generated
from the source XML comments are combined into a static site.

DocFX cannot run this project's Roslyn source generators (the custom object-model `UpdateFromJson`
generator targets a newer Roslyn than DocFX hosts, and `[GeneratedRegex]` partial methods are emitted
the same way), so it does not compile the projects from source. Instead it extracts the API metadata
from the **built Release assemblies**, which already contain the generated code and ship their XML
documentation files (the `Release` configuration sets `DocumentationFile`; `Debug` does not). Build
the projects in Release first, then run DocFX:

```sh
dotnet build src/DuetControlServer/DuetControlServer.csproj -c Release
dotnet build src/DuetWebServer/DuetWebServer.csproj -c Release
docfx src/Documentation/docfx.json --serve
```

Building the two applications also builds `DuetAPI` and `DuetAPIClient`, producing all four
`bin/Release/net10.0/*.dll` plus `*.xml` files that `docfx.json` points its `metadata` step at. The
`modern` template renders the Mermaid diagrams in the articles; because that template fetches the
table of contents and search index, view the result through `docfx serve` (or `--serve`) over HTTP
rather than opening the files directly.

> The assembly paths in `docfx.json` are pinned to `bin/Release/net10.0`. If the target framework is
> bumped, update those four paths to match.

## See also

- [Components](components.md) - what each project builds into
- The repository `README.md` - runtime command-line options and configuration
