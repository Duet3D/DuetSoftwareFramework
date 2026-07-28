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

## NativeAOT builds

Ordinary builds and packages are JIT-compiled; NativeAOT is opt-in. Every build path in this repository
defaults to JIT, and the released packages are built that way.

### AOT versus JIT

AOT compiles to a native executable ahead of time, which removes the JIT and most of the runtime's
start-up work. Measured on a 32-bit ARM SBC against 3.7.0-beta.1, single samples rather than a
controlled benchmark:

| | JIT | AOT |
| --- | --- | --- |
| DuetControlServer `--version` | 393 ms | 46 ms |
| DuetControlServer resident memory | 89.4 MiB | 27.9 MiB |
| DuetWebServer resident memory | 82.5 MiB | 34.4 MiB |
| DuetPluginService resident memory (both instances) | 97.5 MiB | 40.7 MiB |

That is roughly 166 MiB freed across the three core services on an otherwise idle machine. Garbage
collection behaves the same either way; the wins are start-up time, the memory floor, and deployment
size, since each binary carries only the runtime code it actually uses instead of the full shared
`duetruntime`.

What it costs:

- Building takes minutes per project instead of seconds, plus a one-off container image build.
- The build host needs a cross toolchain, see below. A plain `dotnet publish` on a developer machine
  cannot produce an armhf AOT binary.
- Only projects in this repository can be built through `aot/build.sh`; a third-party plugin in its own
  repository needs its own toolchain.
- There is no runtime code generation. Anything reaching reflection-based serialization or dynamically
  created types throws at runtime instead of falling back. The AOT and trim analyzers catch most of it
  at build time, but they cannot see through a path that only a specific machine configuration reaches,
  so a first run on real hardware is part of the test.
- Debug symbols are split into separate `.dbg` files that stay on the build host. Keep the ones matching
  a deployed binary if a crash may need symbolizing later.

### Building

Every executable project declares

```xml
<PublishAot Condition="'$(AotPublish)' == 'true'">true</PublishAot>
```

so a publish becomes a NativeAOT publish by passing `-p:AotPublish=true`. `PublishAot` itself must not
be set on the command line: it is a global property, so it also reaches the `netstandard2.0` targets of
DuetAPI and the source generator, neither of which can be AOT-compiled, and the build fails with
NETSDK1207. All executables also enable the AOT and trim analyzers, so code that would break under AOT
shows up as a warning in an ordinary build rather than as a runtime `NotSupportedException`.

AOT links a real ELF executable for the target, so unlike a JIT publish the build host needs a cross
toolchain: binutils for the target architecture and a sysroot whose glibc is no newer than the
target's. `aot/` holds a Debian bookworm container that provides both (bookworm matches DuetPi's glibc
2.36; `aot/Containerfile` documents the traps) and a script that drives it:

```sh
aot/build.sh --arch=linux-arm64 --build-type=Release
aot/build.sh --arch=linux-arm DuetControlServer
```

Each project is published into its own subdirectory of `aot/out/<arch>/`. Without a project list every
AOT-capable project is built; `linux-arm`, `linux-arm64` and `linux-x64` are supported.

### AOT packages

AOT binaries carry their own runtime, so the shared `duetruntime` package is neither built nor
depended upon. Pass `--aot` to the packager, either through the container or directly on a build host
that already has the toolchain:

```sh
aot/build.sh --arch=linux-arm --deb
pkg/build.sh --aot --target-arch=armhf deb
```

The `duetruntime` dependency is stripped from the `duetcontrolserver`, `duettools` and `duetwebserver`
control files. AOT and JIT packages of the same version carry the same package names and versions, so
they can only be installed as a complete set. `--aot` is supported for `.deb` packages only.

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

> The exact service names, paths, and any packaging steps are environment-specific. `pkg/build.sh`
> holds the canonical packaging recipes used to build the `.deb` / `.rpm` / Arch packages.

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
