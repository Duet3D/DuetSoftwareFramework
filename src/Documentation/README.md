# Documentation

`Documentation` is the DocFX project that generates the published DSF documentation site under the repository-level `docs/` directory. It ties together API metadata, hand-written articles, and the generated content produced by `DocGen`.

## What This Project Owns

| File or directory | Purpose |
|---|---|
| [docfx.json](docfx.json) | DocFX build configuration and output location. |
| [index.md](index.md) | Root page for the DocFX-generated site. |
| [toc.yml](toc.yml) | Top-level navigation for the generated site. |
| [articles/](articles) | Hand-written conceptual documentation included in the DocFX output. |

## How It Works

`docfx.json` collects metadata from selected C# projects and combines that with markdown content. In the current setup it:

- extracts API metadata from `DuetAPI`, `DuetAPIClient`, `DuetControlServer`, and `DuetWebServer`;
- includes markdown from `articles/`, `index.md`, and the API landing pages;
- writes the finished static site to `../../docs`.

This project covers the generated API and article site. The hand-written developer architecture notes under `docs/devel/` and `docs/architecture/` live outside this project but sit alongside the DocFX output in the published tree.

## Interfaces

| Interface | Details |
|---|---|
| C# projects named in [docfx.json](docfx.json) | Supply API metadata and XML documentation. |
| [../DocGen/README.md](../DocGen/README.md) | Produces generated markdown that can be folded into the wider docs workflow. |
| Published `docs/` tree | Final output consumed by GitHub Pages and local doc readers. |
| RepRapFirmware | No direct build-time dependency, but many articles link into firmware-side docs for cross-repo architecture. |

## Build And Verify

The easiest repository-level path is:

```sh
make doc
```

If you are running DocFX directly, do so from this directory after the referenced assemblies have been built.

## Related Docs

- [../../docs/devel/README.md](../../docs/devel/README.md)
- [../../docs/devel/BUILD_VARIANTS.md](../../docs/devel/BUILD_VARIANTS.md)
- [../DocGen/README.md](../DocGen/README.md)
