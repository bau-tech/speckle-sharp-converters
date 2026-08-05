# Third-Party Notices

This repository is licensed under the Apache License, Version 2.0 (see [`LICENSE`](LICENSE)). It
depends on the third-party packages listed below, grouped by license. This list covers direct
`PackageVersion` entries in [`Directory.Packages.props`](Directory.Packages.props); versions track
that file and may lag behind what a given build actually resolves for transitive-only packages.

## Apache License 2.0

- Dapper 2.1.66
- Serilog 4.0.1
- Serilog.Exceptions 8.4.0
- Serilog.Extensions.Logging 8.0.0
- Serilog.Formatting.Compact 3.0.0
- Serilog.Sinks.Console 6.0.0
- Serilog.Sinks.File 6.0.0
- OpenTelemetry.Exporter.OpenTelemetryProtocol 1.11.1
- OpenTelemetry.Instrumentation.Http 1.11.0
- Grpc.Core 2.44.0 / Grpc.Core.Api 2.44.0 (transitive only, via OpenTelemetry's OTLP exporter; excluded from the merged build output, see `Directory.Packages.props`)
- MinVer 7.0.0
- AwesomeAssertions 8.1.0 (community-maintained Apache-2.0 fork of FluentAssertions, used because FluentAssertions v8+ moved to a commercial license)
- Speckle.DoubleNumerics 4.1.0
- Speckle.Triangle 1.0.0
- Speckle.Objects 3.20.6
- Speckle.InterfaceGenerator 0.9.6
- Speckle.Revit.API 2023.0.0 (compile-time reference only — see "Vendor SDKs" below)

## MIT License

- NUnit 4.5.1
- NUnit.Analyzers 4.12.0
- NUnit3TestAdapter 6.2.0
- Moq 4.20.70 (post-4.20.69, i.e. after the SponsorLink telemetry component was removed)
- Semver 3.0.0
- Glob 1.1.9
- Bullseye 6.1.0
- SimpleExec 12.0.0
- Microsoft.Build 18.4.0
- Microsoft.VisualStudio.SolutionPersistence 1.0.52
- Microsoft.Bcl.AsyncInterfaces 9.0.4
- Microsoft.Extensions.Logging 9.0.0
- Microsoft.Extensions.DependencyInjection 8.0.0
- Microsoft.Extensions.Hosting.WindowsServices 9.0.9
- Microsoft.NETFramework.ReferenceAssemblies 1.0.3
- Microsoft.NET.Test.Sdk 18.4.0
- System.CommandLine 2.0.0-beta4.22272.1
- System.Resources.Extensions 9.0.4
- System.Text.Json 5.0.2
- PolySharp 1.15.0
- ILRepack.FullAuto 1.6.0
- LibTessDotNet 1.1.15 (MIT wrapper; the underlying reference tessellation algorithm it ports is SGI Free Software License B)
- Ara3D.Buffers 1.4.5 / Ara3D.Logging 1.4.5 / Ara3D.Utils 1.4.5
- altcover 9.0.102
- Revit.Async 2.1.1

## BSD 3-Clause License

- CefSharp.Wpf / CefSharp.Wpf.NETCore (compile-time reference only — see "Vendor SDKs" below)

## PostgreSQL License

- Npgsql 9.0.4

## Vendor SDKs — referenced, not redistributed

These are proprietary host-application SDKs. In every case the project references them with
`IncludeAssets="compile; build"` / `ExcludeAssets="runtime"` (or equivalent), so the vendor's
assemblies are only used to compile against and are never copied into build output or the
installers — the connector loads and links against the copy the host application already has
installed.

- **Tekla Open API** (`Tekla.Structures.Model`, `.Catalogs`, `.Drawing`, `.Dialog`, `.Plugins`,
  all 2024.0.4) — Trimble Solutions. Compile-only in every Tekla connector project.
- **Autodesk Revit API** (`Speckle.Revit.API` 2023.0.0, with per-connector `VersionOverride`s for
  2022.0.2.1/2025.0.0) — reference-assembly package published by Speckle Systems;
  `ExcludeAssets="runtime"` on both the net48 and net8.0 references.
- **CefSharp / CefSharp.Wpf.NETCore** (92.0.260, with per-connector `VersionOverride`s) and
  **Microsoft.Web.WebView2** (1.0.1938.49) — `IncludeAssets="compile"` only in the Revit connector
  projects; the connector relies on the Chromium/WebView2 runtime the host Revit version already
  embeds and deliberately does not ship its own (see the comment in
  `Connectors/Revit/Speckle.Connectors.Revit2025/Speckle.Connectors.Revit2025.csproj` about not
  distributing vulnerable CEF binaries).
- **JetBrains.Profiler.SelfApi** 2.5.12 — MIT-licensed API wrapper; the proprietary
  dotTrace/dotMemory profiling engine it can trigger is downloaded on demand under JetBrains' own
  terms. Used only by `Sdk/Speckle.Performance`, a developer-only profiling helper not referenced
  by any shipped connector.
