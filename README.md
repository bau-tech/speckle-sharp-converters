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
  - [`Tekla Connector`](https://github.com/bau-tech/speckle-sharp-converters/tree/main/Connectors/Tekla): for Trimble Tekla Structures 2023 - 2025
- **Speckle Converters**
  - [`Revit Converter`](https://github.com/bau-tech/speckle-sharp-converters/tree/main/Converters/Revit)
  - [`Tekla Converter`](https://github.com/bau-tech/speckle-sharp-converters/tree/main/Converters/Tekla)
- **Common**
  - [`Connectors.Common`](https://github.com/bau-tech/speckle-sharp-converters/tree/main/Sdk/Speckle.Connectors.Common): Common connector utilities, and dependency injection.
  - [`Connectors.Logging`](https://github.com/bau-tech/speckle-sharp-converters/tree/main/Sdk/Speckle): OTEL.


## Tekla <-> Revit structural round-trip

The Tekla and Revit connectors support a native structural round-trip with each other (`NativeTekla`/`NativeRevit` receive modes): objects sent from one app are received into the other as real, native elements (not generic DirectShapes), and are recognized across re-sends so a resend updates the existing element in place (or, where the host API has no in-place boundary edit, deletes and recreates it) instead of duplicating it. Elements removed at the source are deleted on receive.

Supported categories, both directions:

- Beams and columns (straight and curved/arc)
- Foundations (pad, strip, and wall footings)
- Walls (straight and curved/arc), including boolean-cut openings (Tekla -> Revit) and literal Revit Opening elements (Revit -> Tekla)
- Floors/slabs

Non-rectangular profiles and materials that can't be auto-resolved are handled via a receive-time mapping dialog, with the mapping persisted for reuse.

# Developing and Debugging

## Developing

To build solutions in this repo, [10.0.2xx of the .NET SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) is required.

It is recommended to use Jetbrains Rider (version 2025.3 or greater) or Visual Studio 2026 (version 18.4 or greater)

From there you can open the main `Speckle.Connectors.slnx` solution and build the project.

For good development experience and environment setup, you the commands are available needed.

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




