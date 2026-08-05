<h1 align="center">
  <img src="Images/logo.svg" width="150px"/><br/>
  Revit ⟷ Tekla Converter
</h1>

<h3 align="center">
    .NET Desktop UI, Connectors, and Converters
</h3>

# Repo structure

This is a private Revit <-> Tekla focused fork, built on top of [Speckle](https://speckle.systems)'s open-source (Apache-2.0) next-generation .NET connector/converter framework:

- **Desktop UI**
  - [`DUI3`](https://github.com/bau-tech/speckle-sharp-converters/tree/main/DUI3): our next generation Desktop User Interface for all connectors.
- **Speckle Connectors**
  - [`Revit Connector`](https://github.com/bau-tech/speckle-sharp-converters/tree/main/Connectors/Revit): for Autodesk Revit 2023 - 2027
  - [`Tekla Connector`](https://github.com/bau-tech/speckle-sharp-converters/tree/main/Connectors/Tekla): for Trimble Tekla Structures 2023 - 2026
- **Speckle Converters**
  - [`Revit Converter`](https://github.com/bau-tech/speckle-sharp-converters/tree/main/Converters/Revit)
  - [`Tekla Converter`](https://github.com/bau-tech/speckle-sharp-converters/tree/main/Converters/Tekla)
- **Common**
  - [`Connectors.Common`](https://github.com/bau-tech/speckle-sharp-converters/tree/main/Sdk/Speckle.Connectors.Common): Common connector utilities, and dependency injection.
  - [`Connectors.Logging`](https://github.com/bau-tech/speckle-sharp-converters/tree/main/Sdk/Speckle.Connectors.Logging): OTEL.

> [!IMPORTANT]
> This connector cannot run alongside the official Speckle Manager connector for the same Revit/Tekla version. Both share the same plugin identity and dependency assemblies, so the host app fails to load either one when both are installed. **Uninstall the official Speckle connector before installing this fork's build** (and vice versa).

## Tekla <-> Revit structural round-trip

The Tekla and Revit connectors support a native structural round-trip with each other (`NativeTekla`/`NativeRevit` receive modes): objects sent from one app are received into the other as real, native elements (not generic DirectShapes), and are recognized across re-sends so a resend updates the existing element in place (or, where the host API has no in-place boundary edit, deletes and recreates it) instead of duplicating it. Elements removed at the source are deleted on receive.

Supported categories, both directions:

- Beams and columns (straight and curved/arc)
- Foundations (pad, strip, and wall footings)
- Walls (straight and curved/arc), including boolean-cut openings (Tekla -> Revit) and literal Revit Opening elements (Revit -> Tekla)
- Floors/slabs

Non-rectangular profiles and materials that can't be auto-resolved are handled via a receive-time mapping dialog, with the mapping persisted for reuse.

## IFC → Native Revit/Tekla Objects

Beyond the Tekla<->Revit round-trip, both the Revit connector and the Tekla connector (**Tekla 2025 and 2026 only** - 2023/2024 aren't wired up) can reconstruct native elements from **any** IFC-sourced model (from Revit, Tekla, ArchiCAD, or any other IFC-exporting tool), not just direct Tekla<->Revit sends. On receive, it re-parses the original `.ifc` file (fetched from the Speckle server's blob storage) and uses the authored geometry/placement data to enrich the already-received generic objects, so they convert to real native elements instead of falling back to `DirectShape`. The extraction/enrichment logic is entirely shared between the two connectors - only the final "build a native element" step is host-specific.

Supported categories:

- Columns and beams/members (straight and curved/arc; rectangular, circular, and other catalog profiles via the mapping dialog), with cross-section rotation and material resolved from the IFC data where available
- Walls (straight and curved/arc axes, material-layer thickness/height), including cut openings
- Floors, including cut openings
- Foundations: isolated pad footings (`IfcFooting`), and pile caps/piles exported as `IfcSlab`
- Grids

Any other element type with displayable geometry but no dedicated converter still gets built as a real, correctly shaped native part where the host API allows it (Tekla: a faceted-BREP shape via its Shape Catalog), rather than falling back to a generic placeholder.

Structured cross-section data isn't always present in the source IFC (e.g. a beam with mitered end cuts, or a Tekla part whose export fell back to a raw mesh because it has an opening cut into it) - in these cases a receive-time mapping dialog (its own dialog/mapping table per connector - Revit's `IfcTypeMappingDialog` resolves to a family type, Tekla's `IfcProfileMappingDialogService` to a catalog profile string) lets the element be resolved manually, and a handful of geometry-derived fallbacks (e.g. inferring a wall's axis from its body's bounding box) recover what they can automatically. When neither succeeds, the element keeps its original `DirectShape` representation - this feature only ever adds fidelity, never replaces working geometry with a guess.

This code lives in `Converters/Ifc/Speckle.Converters.IfcShared` (host-agnostic STEP parsing/extraction/enrichment, targeting `net8.0;net48` so both Revit's and Tekla's TFMs can reference it) and only activates for versions whose source application was IFC; it has no effect on native Revit or Tekla sends.

# Developing and Debugging

## Developing

To build solutions in this repo, [8.0.417 of the .NET SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) is required, as pinned in `global.json` (this fork pins to 8.x rather than upstream's 10.x SDK requirement).

It is recommended to use Jetbrains Rider (version 2025.3 or greater) or Visual Studio 2026 (version 18.4 or greater)

From there you can open the main `Speckle.Connectors.slnx` solution and build the project.

### Formatting
We're using [CSharpier](https://github.com/belav/csharpier) to format our code. You can use Csharpier in a few ways:
- Install CSharpier and reformat from CLI
  ```
  dotnet tool restore
  dotnet csharpier format ./
  ```
- Install the CSharpier extension for [Rider](https://plugins.jetbrains.com/plugin/18243-csharpier) or [Visual Studio](https://marketplace.visualstudio.com/items?itemName=csharpier.CSharpier)<br/>
  For best DX, we recommend turning on CSharpier's `reformat on save` setting if you've installed it in your IDE.

## Build Commands

### Clean Locks
We're using package locks to store exact and versioned dependency trees. Occasionally you will need to clean your local package-lock files, eg when switching between `Speckle.Connectors.slnx` and `Local.slnx`.
Run this command in CLI to delete all package.lock.json files before a restore:
```
.\build.ps1 clean-locks
```

### Deep Clean
To make sure your local environment is ready for a clean build, run this command to delete all `bin` and `obj` directories and restore all projects:
```
.\build.ps1 deep-clean
```
### Deep Clean Local

This is for users of the `Local.slnx` solution:

To make sure your local environment is ready for a clean build, run this command to delete all `bin` and `obj` directories and restore all projects:
```
.\build.ps1 deep-clean-local
```

## Local development with SDK changes
If you'd like to make changes to the [`speckle-sharp-sdk`](https://github.com/specklesystems/speckle-sharp-sdk) side-by-side with changes to this repo's projects, use `**Local.slnx**`. <br/>
This solution includes the Core and Objects projects from the speckle-sharp-sdk repo, and uses a new Configuration to create a build directory alongside `Debug` and `Release`.

> [!WARNING]
> Using `Local.slnx` will modify all your package locks. **Don't check these in!** Revert with the `clean-locks` command or use the regular solution to revert once your changes are made.


# Security and Licensing

### Security

For any security vulnerabilities or concerns regarding this fork, please open a private security advisory on this repository.

### License

Unless otherwise described, the code in this repository is licensed under the Apache-2.0 License, inherited from the upstream [specklesystems/speckle-sharp-connectors](https://github.com/specklesystems/speckle-sharp-connectors) project this fork is based on. Please note that some modules, extensions or code herein might be otherwise licensed. This is indicated either in the root of the containing folder under a different license file, or in the respective file's header.

See [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) for the licenses of third-party dependencies and vendor SDKs referenced by this repo.




