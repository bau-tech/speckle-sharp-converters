# Revit & Tekla Structures Connectors — Functional Description

This document describes the Speckle connectors for Autodesk Revit and Tekla Structures: their individual architecture (Part A, Part B), and the round-trip and cross-application workflows they support together (Part C).

---

# Part A — Revit Connector

Covers `Connectors/Revit/*` and `Converters/Revit/*` (currently shipping for Revit 2023–2027).

## A.1 Architecture overview

The connector follows the same **shared-project pattern** used across Speckle's connectors: thin, version-specific `.csproj` shells import large shared projects (`.shproj`/`.projitems`) compiled against a particular Revit SDK version.

- **Connector side:**
  - `Speckle.Connectors.RevitShared` — bindings, DI, host-app services, send/receive operations, plugin bootstrap.
  - `Speckle.Connectors.RevitShared.Cef` — CefSharp-based dockable panel, imported by Revit 2023/2024/2025.
  - `Speckle.Connectors.RevitShared.WebView` — WebView2-based dockable panel, imported by Revit 2026/2027 instead of CefSharp.
  - `Speckle.Connectors.Revit.Common` — a real (non-shared) project referenced by every version shell: `AssemblyResolver`, `RevitTask`/`RevitAsync`.
  - Version shells `Speckle.Connectors.Revit2023` … `Revit2027`, each importing the shared projects and referencing the matching converter project. (Revit2022 support has been dropped; the folder remains but has no active `.csproj`.)
- **Converter side:** `Speckle.Converters.RevitShared` (`ToSpeckle/`, `ToHost/`, `Helpers/`, `Services/`, `Settings/`, `Extensions/`), plus per-version shells `Speckle.Converters.Revit2022` … `Revit2027`.

**Conditional compilation.** Each version defines a primary symbol plus cumulative symbols, e.g. Revit2025 defines `REVIT2025;REVIT2023_OR_GREATER;REVIT2024_OR_GREATER;REVIT2025_OR_GREATER`. `RevitExternalApplication.GetVersion()` switches on these to resolve `HostAppVersion` for DI init.

**Target frameworks:** `net48` (2023/2024), `net8.0-windows` (2025/2026), `net10.0-windows` (2027); all `x64`, `UseWpf=true`.

**WebView/Cef split.** `RevitConnectorModule.RegisterUiDependencies()` branches on `#if !REVIT2026_OR_GREATER` to register either `RevitCefPlugin`/`CefSharpPanel` (CefSharp.Wpf / CefSharp.Wpf.NETCore) or `RevitWebViewPlugin`/`RevitControlWebView` (Microsoft.Web.WebView2) as the active `IRevitPlugin`. Both do the same job — create the ribbon tab/button, register the dockable pane, hook `ApplicationInitialized` to init `RevitAsync`.

## A.2 Plugin host & UI integration

- **`.addin` manifests** (`Connectors/Revit/Speckle.Connectors.RevitShared/Plugin/*.addin`, one per version) point `FullClassName` at `Speckle.Connectors.Revit.Plugin.RevitExternalApplication`, with a fixed `ClientId`/`VendorId="speckle"`. The registered app name is **"Speckle Revit Connector New UI"**, deliberately distinct from the older DUI2 connector so both can be installed side by side.
- **`RevitExternalApplication`** (`IExternalApplication`) — `OnStartup` hooks `AssemblyResolve`, builds a `ServiceCollection`, calls `services.Initialize(HostApplications.Revit, GetVersion())`, `AddRevit()`, `AddRevitConverters()`, builds the provider, calls `UseDUI()`, initializes `RevitAsync`, resolves `IRevitPlugin` and calls `Initialise()`. Declares a fixed `DockablePaneId`, distinct from the official connector's pane.
- **`RevitCommand`** (`SpeckleRevitCommand : IExternalCommand`) — the ribbon button handler; shows the dockable pane.
- **`RevitIdleManager`**, **`RevitTask`/`RevitThreadContext`** — marshal work onto Revit's API/main thread; used throughout bindings.
- Ribbon UI: a **"Speckle Converter"** tab (deliberately different from the shared `"Speckle"` tab used by other connectors), one push button, tries/catches `ArgumentException` on tab creation (in case DUI2+DUI3 are both installed).

## A.3 Connector bindings

All in `Connectors/Revit/Speckle.Connectors.RevitShared/Bindings/`:

| Binding | Responsibility |
|---|---|
| `BasicConnectorBindingRevit` | `IBasicConnectorBinding`: source-app version reporting, `GetDocumentInfo()`, model-card CRUD, and **highlight/zoom** (`HighlightModel`/`HighlightObjects` — selects + `ShowElements` on the active view). |
| `SelectionBinding` | `ISelectionBinding`: subscribes to `UIApplication.SelectionChanged` (via `RevitIdleManager`), pushes `SelectionInfo` (element `UniqueId`s + summary) to the frontend. |
| `RevitSendBinding` | `ISendBinding`: send filters/settings, `Send(modelCardId)`, `UpdateParameters`, document-change subscription/eviction via `RevitSendChangeTracker`, send-filter selection resolution including linked models and rooms/areas. |
| `RevitReceiveBinding` | `IReceiveBinding`: receive settings, `Receive(modelCardId)` (builds `RevitConversionSettings`, detail level fixed to `Coarse`, resolves `ReceiveMode` via `ToHostSettingsManager`), catches `SpeckleRevitTaskException` for friendly error surfacing. |
| `RevitParametersBinding` | `IParametersBinding`: batch parameter edits — resolves elements by `ApplicationId`/`UniqueId` (rejects linked-model IDs), parses `"Scope.Category.Name"` paths, applies via `ParameterUpdater` inside a transaction with a failures-preprocessor. |

Standard DUI bindings (`TestBinding`, `ConfigBinding`, `AccountBinding`) are also registered.

## A.4 Send pipeline

**Filters** (`Operations/Send/Filters/`): `RevitSelectionFilter` (default), `RevitViewsFilter`, `RevitCategoriesFilter`.

**Root object builders** — two implementations, both in `Operations/Send/`: `RevitRootObjectBuilder` (batch — full in-memory `Collection` tree) and `RevitContinuousTraversalBuilder` (streaming — incremental upload via `SendPipeline`). Shared pipeline:

1. Reject family-environment documents; build a root `Collection` named after the document.
2. Split elements per `DocumentToConvert` (main model vs. linked models).
3. `ElementUnpacker.UnpackSelectionForConversion` — recursively unpacks Groups, arrays, nested `FamilyInstance` sub-components, `MultistoryStairs`; deduplicates; removes known "child" elements (mullions/panels/stacked-wall members) when their parent is also selected.
4. Per element: resolve a cross-app-stable `applicationId` via `RevitOutgoingApplicationIdResolver` (§A.7); check the send-conversion cache (keyed by `UniqueId` + transform hash for linked instances, invalidated by `RevitSendChangeTracker`); convert via `ElementTopLevelConverterToSpeckle`.
5. **Collection hierarchy** — `SendCollectionManager` builds `(model/linked-model name) > Level > Category > Type`.
6. Proxies attached to the root: render-material proxies, level proxies, instance-definition proxies, a `ConversionTable` (§C.1), view/camera proxies, reference-point transform data.

## A.5 ToSpeckle conversion — a single generic converter

Unlike a per-category converter design, **nearly every Revit element funnels through one converter**: `ElementTopLevelConverterToSpeckle` (`ToSpeckle/TopLevel/RevitElementTopLevelConverterToSpeckle.cs`), tagged for `DB.Element`, producing `Speckle.Objects.Data.RevitObject` — the same "generic data object" family as Tekla's `TeklaObject` (Part B). It populates:

- `name`, `type`/`family`, `level`, `category`, `location` (special-cased for `Grid`, `Floor`, `RoofBase`, `Opening`), `elements` (children — curtain mullions/panels/hosted openings), `displayValue`, `properties`, `units`.
- A dynamic `builtInCategory` property (locale-independent, e.g. `"OST_Walls"`) — the key the receiving side dispatches on (§A.6, and §C throughout).
- Only one other top-level converter exists: `View3DTopLevelConverterToSpeckle` (3D views → `Objects.Other.Camera`).

**Property capture** via `PropertiesExtractor`, composing `ClassPropertiesExtractor` (element id, `builtInCategory`, workset, room/space ids, `parentApplicationId` for hosted openings), material quantities, and `ParameterExtractor` (`"Instance Parameters"`, `"Type Parameters"`, `"Structure"` compound-layer breakdown, `"System Type Parameters"`; filters out `"_ID"`-suffixed BuiltInParameters since raw element-IDs aren't portable).

## A.6 Receive pipeline & ToHost conversion

**Receive modes** (`enum ReceiveMode`, `Settings/RevitConversionSettings.cs`): `DirectShape` (generic geometry, always works), `NativeRevit` (native families/structural elements from a Revit-sourced model), `NativeTekla` (native structural elements from a Tekla-sourced model — see Part C for the dispatch table).

Mode is **auto-detected** (`ToHostSettingsManager.GetReceiveMode`): if "Receive as Native Elements" is on, the model card's `SelectedVersionSourceApp` decides — contains `"tekla"` → `NativeTekla`, else → `NativeRevit`; off → always `DirectShape`.

**`RevitHostObjectBuilder.BuildSync`:**
1. Pre-receive cleanup (reset `DirectShapeLibrary`, clear caches, purge Speckle-managed materials).
2. `RootObjectUnpacker.Unpack`; pick `FamilyUnpackStrategy` (`NativeRevit`) or `DirectShapeUnpackStrategy` (flat, for `NativeTekla`/`DirectShape`).
3. Tekla profile-mapping dialog if needed (§C.1).
4. **Bake** — `RevitRootToHostConverter.Convert` per object (§A.7 for dispatch); deletion passes for previously Speckle-managed Beams/Columns/Foundations/Walls/Floors/Openings absent from the payload.
5. `NativeRevit`-only: `BakeInstancesAsFamilies`.
6. Post-bake material painting.

**`RevitRootToHostConverter`** dispatch (`NativeRevit` mode) — `TryNativeRevitConvert` switches on the `builtInCategory` property:

| `builtInCategory` | Converter |
|---|---|
| `OST_Grids` | `GridToHostConverter` |
| `OST_StructuralFraming` | `BeamToHostConverter` |
| `OST_StructuralColumns` | `ColumnToHostConverter` |
| `OST_Walls` | `WallToHostConverter` |
| `OST_Floors` | `FloorToHostConverter` |
| `OST_StructuralFoundation` | `FoundationToHostConverter` |
| `OST_Roofs` | `RoofToHostConverter` |
| *(contains)* `"Opening"` | `OpeningToHostConverter` |

Anything unmatched (MEP, most family instances, etc.) falls through to **DirectShape** — Revit's generic geometry-only fallback (`_baseToGeometryConverter.Convert` → `DirectShapeLibrary.AddDefinition`). `NativeTekla`-mode dispatch (`TryNativeTeklaConvert`, handling incoming `TeklaObject`s directly) is covered in Part C, §C.4/C.5, since it only matters for cross-app receives.

## A.7 Identity preservation

- **`OriginApplicationIdSchema`** (`Helpers/OriginApplicationIdSchema.cs`) — a fixed-GUID ExtensibleStorage schema storing one string field (`OriginApplicationId`) directly on the `Element`: the Speckle `applicationId` the element carried the very first time it was ever received.
- **Per-category existing-element indexes** (`Helpers/`): `RevitExistingBeamIndex` (Beams/Columns/Foundations), `RevitExistingWallIndex`, `RevitExistingFloorIndex`, `RevitExistingOpeningIndex`. Each indexes elements by both `Element.UniqueId` and the stamped origin id, letting converters reposition/re-type an existing element in place instead of duplicating it, and re-stamp on update.
- **Deletion tracking** — `GetDeletionCandidates()` returns previously Speckle-managed elements (carrying the stamp) absent from the current payload; only elements that have round-tripped at least once are ever candidates, so unrelated native content is never at risk.
- **Send-side symmetry** — `RevitOutgoingApplicationIdResolver` resolves the applicationId to emit: the origin-id stamp if present (and not already claimed this send, guarding against native "Copy" duplicating ExtensibleStorage data), else the element's own `UniqueId`.
- `FloorToHostConverter` specifically compares footprints to choose cheap in-place update vs. delete-and-recreate, since `DB.Floor` has no simple boundary-edit API.

## A.8 Settings

`RevitConversionSettings` (record): `Document`, `DetailLevel`, `ReferencePointTransform`, `SpeckleUnits`, `SendParameterNullOrEmptyStrings`, `SendLinkedModels`, `SendRebarsAsVolumetric`, `SendAreasAsMesh`, `ReceiveMode` (default `DirectShape`), `Tolerance` (≈5mm).

**Send card settings:** `DetailLevelSetting` (default Medium), `SendReferencePointSetting` (default InternalOrigin), `SendParameterNullOrEmptyStringsSetting` (default false), `LinkedModelsSetting` (default true), `SendRebarsAsVolumetricSetting` (default false), `SendAreasAsMeshSetting` (default false), `AppendRoomsAndAreasSetting` (default None).

**Receive card settings:** `ReceiveReferencePointSetting` (default Source), `ReceiveInstancesAsFamiliesSetting` — title **"Receive as Native Elements"**, default **true** — the toggle that (combined with source-app auto-detection) drives `ReceiveMode` selection.

## A.9 Supported object types — converter inventory

**ToSpeckle:** the universal `ElementTopLevelConverterToSpeckle` (walls, floors, beams, columns, foundations, roofs, openings, grids, MEP, family instances, rooms/areas — all via inline branching, not separate classes) + `View3DTopLevelConverterToSpeckle`.

**ToHost — one class per native category:** `BeamToHostConverter`, `ColumnToHostConverter`, `FloorToHostConverter`, `FoundationToHostConverter`, `GridToHostConverter`, `OpeningToHostConverter`, `RoofToHostConverter`, `WallToHostConverter`, plus `TeklaGridSystemToHostConverter` (Tekla "Grid" `TeklaObject` → N native `DB.Grid`s). `StructuralFramingHelper` provides shared FamilySymbol/Level resolution for Beams/Columns/Foundations. No dedicated pipe/duct/MEP ToHost converter exists — MEP falls through to DirectShape.

## A.10 Dependency injection summary

**`ServiceRegistration.AddRevit()`** (`Connectors/Revit/Speckle.Connectors.RevitShared/DependencyInjection/RevitConnectorModule.cs`) registers: DUI framework wiring, UI dependencies (Cef/WebView branch), all bindings, send-side services (`ElementUnpacker`, `LevelUnpacker`, `ConversionTableUnpacker`, `ViewUnpacker`, `SendCollectionManager`, both root object builders, `SendConversionCache`, settings managers, `RevitSendChangeTracker`), receive-side services (`RevitHostObjectBuilder`, `RevitFamilyBaker`, `RevitMaterialBaker`, unpack strategies, `RevitPreBakeSetupService`, `TeklaProfileMappingDialogService`), default traversal, progress management.

**`ServiceRegistration.AddRevitConverters()`** (`Converters/Revit/Speckle.Converters.RevitShared/ServiceRegistration.cs`) registers: root converters, unit converter, `RevitRootToHostConverter`, caches, converter settings store, `ReferencePointConverter`, `RevitElementTypeResolver`, the existing-element indexes, `RevitOutgoingApplicationIdResolver`, `TeklaProfileMappingProvider`, all ToHost converters, property extractors.

---

# Part B — Tekla Structures Connector

Covers `Connectors/Tekla/*` and `Converters/Tekla/*` (currently shipping for Tekla 2025, sharing the bulk of its implementation with the 2023/2024 variants).

## B.1 Architecture overview

The connector follows the same shared-project pattern:

- `Connectors/Tekla/Speckle.Connector.Tekla2025` and `Converters/Tekla/Speckle.Converter.Tekla2025` — version-specific shells referencing the Tekla 2025 assemblies (`Tekla.Structures.Model`, `.Dialog`, `.Drawing`, `.Plugins`), compiled with `TEKLA2025`. Both target `net48`/x64, since Tekla loads plugins as in-process .NET Framework assemblies.
- `Connectors/Tekla/Speckle.Connector.TeklaShared` — connector bindings, send/receive operations, host-app services, settings, DI registration.
- `Converters/Tekla/Speckle.Converters.TeklaShared` — ToSpeckle and ToHost conversion logic, geometry/property extraction helpers, conversion settings, DI registration.

The connector is built and loaded as a **Tekla plugin** (`.dll`); the Speckle UI is hosted inside Tekla's window manager via a WPF `ElementHost`.

## B.2 Plugin host & UI integration

`SpeckleTeklaPanelHost` is the plugin entry point (subclass of `PluginFormBase`):

- Tekla invokes plugin forms twice on first activation; the host closes itself on the first invocation and performs full setup on the second (`InitializeInstance`).
- Connects to the active `Tekla.Structures.Model.Model`, builds the DI container (`AddTekla()` + `AddTeklaConverters()`), instantiates `DUI3ControlWebView`, embeds it via a Windows Forms `ElementHost` parented to Tekla's main window frame.
- A static `IsInitialized` flag prevents duplicate panels; closing resets it so the panel can be reopened.

## B.3 Connector bindings

| Binding | Responsibility |
|---|---|
| `TeklaBasicConnectorBinding` | Source app name/version, document info, **highlight/zoom** (resolves GUIDs → `ModelObject`s, selects, zooms to bounding box). |
| `TeklaSendBinding` | Orchestrates send (§B.4); send filters/settings; subscribes to `ModelObjectChanged` to track changes, expire model cards, evict stale cache entries. |
| `TeklaReceiveBinding` | Orchestrates receive (§B.6); resolves receive mode, exposes the receive-mode setting; failures logged and swallowed. |
| `TeklaSelectionBinding` | Mirrors Tekla's live selection: queries `ModelObjectSelector.GetSelectedObjects()`, filters construction objects, pushes `setSelection` on `SelectionChange`. |

Standard DUI bindings (`TestBinding`, `ConfigBinding`, `AccountBinding`) also registered.

## B.4 Send pipeline

**Selection.** `TeklaSelectionFilter` returns selected GUIDs; `TeklaSendBinding.Send()` resolves back to `ModelObject`s via `model.SelectModelObject(new Identifier(guid))`. No automatic traversal beyond the selection — children are picked up during conversion.

**Conversion.** `TeklaRootObjectBuilder` (batch) and `TeklaContinuousTraversalBuilder` (streaming) both:

1. Create a root `Collection` named after the model.
2. Per selected object: compute a deterministic `applicationId` (GUID-based), check the send conversion cache (scoped by project + applicationId), convert via `TeklaRootToSpeckleConverter.Convert()` on a miss.
3. Place converted objects into a type-based collection hierarchy via `SendCollectionManager`.
4. Capture render-material info via `TeklaMaterialUnpacker` (one `RenderMaterialProxy` per distinct ARGB color).
5. Conversion failures logged + written to `%TEMP%\speckle_tekla_send_error.txt`; don't stop the overall send.

**Cache invalidation:** on `ModelObjectChanged`, or on toggling "Send rebars as solid" (evicts only affected rebar objects).

## B.5 Conversion to Speckle (ToSpeckle)

Every converted `ModelObject` becomes a `Speckle.Objects.Data.TeklaObject`. `ModelObjectToSpeckleConverter` builds each from:

- **type** — CLR class name (`Beam`, `BoltArray`, `RebarGroup`, …).
- **name** — `Part.Name`/`Reinforcement.Name`, falling back to the type name.
- **elements** — recursively converted children (`GetSupportedChildren()`), forming a nested tree (a `Beam` contains its bolts, welds, cuts, chamfers, boolean ops, reinforcement).
- **properties** — a flat dictionary from `ClassPropertyExtractor`: profile/material/class/finish, position, numbering, phase, `assembly_id`/`is_main_part`, plus type-specific data for bolt groups (pattern, hole/thread/tolerance, `mainPartId`/`partToBoltToId`/`secondaryPartIds`), reinforcement (grade/size/class, hooks, leg faces, guidelines — including full `RebarMesh`/`RebarSet` definitions), sub-components (fitting planes, chamfer geometry, boolean data, weld `main_id`/`secondary_id`), and grids (coordinate strings, labels, extensions).
- **location** — type-dependent primary reference geometry: `Line` for beams, `Polyline` for plates/reinforcement contours, first leg face's contour for `RebarSet`, `null` where no simple representation exists.
- **displayValue** — type-dependent visualization: solid-derived `Mesh` for parts/bolt groups/rebar meshes; for reinforcement, either a `Mesh` or centerline `Line`/`Arc` depending on the **"Send rebars as solid"** setting.
- **applicationId**/**units** — the Tekla GUID and the project's Speckle unit string.

## B.6 Receive pipeline

`TeklaReceiveBinding` resolves the active receive mode (Native/Generic) via `TeklaToHostSettingsManager`. `TeklaHostObjectBuilder` runs a **three-pass build**:

1. **Main parts.** The tree is flattened to atomic `TeklaObject`s (recursing through `Collection`s). Sub-component types (bolt groups, welds, seams, fittings, boolean parts, cut planes, chamfers, reinforcement) are skipped here. Each remaining "main part" converts via `TeklaRootToHostConverter`; its `assembly_id`/`is_main_part` flags are captured; the result is registered in `TeklaReceiveCache`.
2. **Sub-components.** Walked again; for each object with children, the parent is looked up in the cache and `SubComponentToHostConverter.ConvertAndAttach()` builds/attaches the sub-object.
3. **Assemblies.** Parts sharing `assembly_id` are grouped; secondary parts added to the main part's `Assembly`.

`model.CommitChanges()` applies everything. `TeklaReceiveCache` is dual-keyed (Speckle id **and** original `applicationId`), allowing both fresh receives and "replace existing" receives to resolve parents.

## B.7 Conversion to Tekla (ToHost)

**`TeklaRootToHostConverter`** is the dispatcher (verified current behavior — see also Part C for the cross-app paths):

```csharp
public object Convert(Base target)
{
    if (target is TeklaObject teklaObject) { /* dispatch by type: Beam/Column, ContourPlate, PolyBeam,
        BentPlate, SpiralBeam, LoftedPlate, Grid, RadialGrid → dedicated converter */ }

    if (target is RevitObject revitObject) { /* dispatch by builtInCategory — see Part C, §C.4 */ }

    // any other Base (other host apps, e.g. Objects.BuiltElements.* types): ReceiveMode.Native tries
    // the generic built-element converter first, falls back to the placeholder converter on failure;
    // ReceiveMode.Generic always uses the placeholder converter directly.
}
```

Common part properties (profile, material, class, finish, position, numbering, phase, end-point offsets, UDAs) are applied uniformly by `TeklaPartPropertyApplicator`. `SubComponentToHostConverter` builds/attaches sub-objects onto an already-created parent: bolt groups (reconstructing `PartToBeBolted`/`PartToBoltTo`/`OtherPartsToBolt`), welds (`MainObject`/`SecondaryObject`, skipped if either side can't resolve), fittings, boolean parts/cuts, cut planes, chamfers, and reinforcement (`SingleRebar`/`RebarGroup` polygon reconstruction, `RebarMesh` full reconstruction, `RebarSet` with `RebarSpacing.Create(...)` factory construction).

`GeometricItemToHostConverter` is the last-resort fallback for any `Base` without a specific converter: builds a placeholder `TSM.Beam` ("Generic Placeholder", profile `HEA200`) carrying the object's display meshes, so geometry from unsupported sources is still visible.

## B.8 Identity preservation

- **`TeklaOriginIdentifier`** — a UDA on the `ModelObject`, the Tekla-side equivalent of Revit's `OriginApplicationIdSchema` (§A.7): stores the Speckle `applicationId` an element carried the first time it was ever received.
- **`TeklaExistingBeamIndex`** (`Converters/Tekla/Speckle.Converters.TeklaShared/ToHost/TeklaExistingBeamIndex.cs`) — per-receive lookup of existing `TSM.Beam` parts, indexed by both native Tekla GUID (a Tekla-authored beam's own applicationId on send *is* this GUID, so it matches on the very first round-trip with no stamp needed) and the stamped origin id (needed when the beam originated in a different app). Tracks which indexed beams carry the stamp ("Speckle-managed") vs. which were matched this receive, so `GetDeletionCandidates()` can report Speckle-managed beams absent from the payload — only content Speckle has touched before is ever eligible for delete-on-source-removal.

## B.9 Settings

| Setting | Scope | Purpose |
|---|---|---|
| **Send rebars as solid** (`SendRebarsAsSolidSetting`, bool) | Send | Solid-derived `Mesh` (`true`) vs. centerline `Line`/`Arc` (`false`). Toggling evicts affected send-cache entries. |
| **Receive mode** (`ReceiveModeSetting`, enum Native/Generic) | Receive | Native: cross-app `BuiltElements` via dedicated converters with fallback to generic; Generic: always the placeholder converter. Auto-resolves to Native unless overridden. |

`TeklaConversionSettings` bundles the active `Model`, rebar/receive-mode choices, and the Speckle unit string derived from Tekla's current display unit (the internal model is always millimeters).

## B.10 Tekla object types supported

| Tekla type | Speckle representation | Notes |
|---|---|---|
| `Beam`, `Column` | `TeklaObject` | Line geometry + profile/material; Tekla treats columns as beams |
| `ContourPlate` | `TeklaObject` | Polyline contour incl. chamfer metadata |
| `PolyBeam` | `TeklaObject` | Multi-segment beam from contour |
| `BentPlate` | `TeklaObject` | Geometry only via `ConnectiveGeometry`; no extractable `location` |
| `SpiralBeam` | `TeklaObject` | Spiral parameters (rise, rotation/twist angles, axis) |
| `LoftedPlate` | `TeklaObject` | Multiple base curves + face type |
| `Grid`, `RadialGrid` | `TeklaObject` | Coordinate strings, labels, extensions |
| `BoltArray`/`BoltCircle`/`BoltXY` | sub-component | Pattern-specific data; connection captured via `mainPartId`/`partToBoltToId`/`secondaryPartIds` |
| `Weld`, `Seam` | sub-component | `main_id`/`secondary_id` cache lookup |
| `Fitting`, `CutPlane`, `EdgeChamfer`, `BooleanPart` | sub-component | Plane-/operation-based geometry on a parent part |
| `SingleRebar`, `RebarGroup`, `RebarMesh`, `RebarSet` | sub-component (reinforcement) | Full grade/size/class/spacing/geometry capture; attached via `father_id` |
| `RevitObject` (cross-app) | native part or placeholder | See Part C |
| Anything else unsupported | `Base` | `GeometricItemToHostConverter` placeholder beam |

## B.11 Dependency injection summary

`ServiceRegistration.AddTekla()` registers: browser bridge, idle manager, DUI wiring, all bindings, Tekla API singletons (`Model`, `Events`, `ModelObjectSelector`), send pipeline (`TeklaSelectionFilter`, `SendConversionCache`, `SendCollectionManager`, both root builders, `SendOperation`), receive pipeline (`TeklaHostObjectBuilder`, `TeklaMaterialUnpacker`), settings managers, converter settings store, default traversal, progress management, plus all `IToSpeckleTopLevelConverter` implementations.

`ServiceRegistration.AddTeklaConverters()` registers: extraction helpers (`DisplayValueExtractor`, `ClassPropertyExtractor`, `ReportPropertyExtractor`, `UserDefinedAttributesExtractor`, `PropertiesExtractor`, `LocationExtractor`), root ToSpeckle/ToHost converters, all type-specific ToHost converters, the receive cache, sub-component converter, generic/cross-app converters, unit converter, converter settings store.

---

# Part C — Workflows & Interoperability

Four workflows are in scope: **Revit → Speckle → Revit**, **Tekla → Speckle → Tekla** (native round-trips, detailed in Parts A/B above), and two cross-app round-trips detailed below.

## C.1 The interop contract

Interoperability works **without either connector assembly referencing the other's types** — both depend only on `Speckle.Objects.Data`, and each receive side reads dispatch keys from the other's generic Data object:

- Revit emits `RevitObject` with a `builtInCategory` dynamic property (§A.5).
- Tekla emits `TeklaObject` with `type`/`class` properties (§B.5).
- Tekla's `TeklaRootToHostConverter.Convert` recognizes an incoming `RevitObject` directly (§B.7, §C.4).
- Revit's `RevitRootToHostConverter.TryNativeTeklaConvert` recognizes an incoming `TeklaObject` directly (§A.6, §C.5).

**Identity preservation across hops** — see §A.7 (Revit) / §B.8 (Tekla). Because each stamp (`OriginApplicationIdSchema` / `TeklaOriginIdentifier`) is *additive* — a hop into the other app never overwrites the original origin id, it just adds its own native identifier alongside — a 4-hop chain (Revit→Speckle→Tekla→Speckle→Revit) converges instead of duplicating: each hop looks up "have I already baked something claiming this origin id?" before inserting.

**Profile / material mapping dialogs** — a native round-trip never needs one (the receiving app already knows its own family/type or Tekla profile); a cross-app hop does, since "profile"/"material" are free-text strings in one app's vocabulary needing a human decision to map into the other's catalog:

| Direction | Dialog | Behavior |
|---|---|---|
| Tekla → Revit | `TeklaProfileMappingDialog` (`Connectors/Revit/.../Operations/Receive/ProfileMapping/`) | One row per distinct `(category, Tekla profile)` pair that can't auto-resolve. Rectangular profiles (`"{h}*{w}"`) auto-synthesize a matching family/type and are skipped; **foundations always get a row** (a footing's profile is a plan footprint, not a cross-section — no reliable auto-sizing heuristic). Editable dropdown of loaded `FamilySymbol`s only. Persisted at `%AppData%\Speckle\Revit\tekla-profile-mapping.json`. |
| Revit → Tekla | `ConversionMappingDialog` (`Connectors/Tekla/.../Operations/Receive/ConversionMapping/`) | Rows are the union of the sender's attached `ConversionTable` and a scan of the actually-received `RevitObject`s (so commits sent before the table existed still prompt). Maps Revit family/type or structural material to a Tekla catalog profile/material, live-validated via `TeklaCatalogValidator`. Persisted via `RevitProfileMaterialMappingProvider`. |

Both are modal, shown at most once per receive (only if unresolved rows exist), with an optional "save as default" to persist choices.

The **`ConversionTable`** is a lightweight inventory Revit attaches to the root object on every send (`ConversionTableUnpacker`): distinct structural family/types (with width/height-mm hints for rectangular sections) and distinct structural material names, scoped to categories Tekla can natively receive. Best-effort — a send never fails because of it.

## C.2 Workflow: Revit → Speckle → Revit

See §A.4–§A.7. Summary: `ElementTopLevelConverterToSpeckle` → `RevitObject` → `TryNativeRevitConvert` dispatches on `builtInCategory` to the matching ToHost converter → existing-element indexes update in place → deletion passes remove stale elements. No mapping dialog needed.

## C.3 Workflow: Tekla → Speckle → Tekla

See §B.4–§B.8. Summary: `ModelObjectToSpeckleConverter` → `TeklaObject` tree → three-pass `TeklaHostObjectBuilder` (main parts, sub-components, assemblies) → `TeklaExistingBeamIndex`-style matching by native GUID. No mapping dialog needed.

## C.4 Workflow: Revit → Speckle → Tekla → Speckle → Revit

Revit-authored elements travel to Tekla and back.

**Hop 1 — Revit → Speckle → Tekla.**
1. Revit sends `RevitObject`s (§A.5).
2. Tekla receives: `TeklaRootToHostConverter.Convert` sees a `RevitObject` and dispatches on `builtInCategory`:

   | `builtInCategory` | Converter | Notes |
   |---|---|---|
   | `OST_Walls` | `RevitWallToTeklaBeamConverter` | |
   | `OST_Floors` | `RevitFloorToContourPlateConverter` | |
   | `OST_StructuralColumns`, `OST_StructuralFraming` | `RevitColumnBeamToTeklaBeamConverter` | Straight elements → `TSM.Beam`; point-placed columns synthesize a vertical segment from the column's height; curved axes → `TSM.PolyBeam`. |
   | `OST_StructuralFoundation` | `RevitFoundationToTeklaConverter` | Point/line footings become sized concrete beams (dimensions from the element's `displayValue` bounding box, deliberately not family parameter names — locale-dependent, e.g. German "Breite"/"Höhe"); slab-like foundations delegate to the floor converter. |
   | *(contains)* `"Opening"` | `RevitOpeningToBooleanPartConverter` | → `TSM.BooleanPart` cut on the already-converted host (resolved via `parentApplicationId` + `TeklaReceiveCache`), not a standalone part. |
   | `OST_StructuralFramingSystem` | *(skipped)* | Sketch container has no independent geometry — member beams arrive separately. |
   | anything else | *(unhandled → generic/placeholder fallback)* | |

3. **Profile/material mapping** — `ConversionMappingDialog` prompts once for unresolved rows.
4. **Identity** — first arrival in Tekla: no GUID match; the new part is stamped with `TeklaOriginIdentifier` = the Revit element's origin id.

**Hop 2 — Tekla → Speckle → Revit.**
5. Tekla sends the (possibly edited) parts back as `TeklaObject`s (`applicationId` = Tekla GUID; `class`/`type` now reflect Tekla's vocabulary).
6. Revit receives with `ReceiveMode.NativeTekla` (auto-detected). `TryNativeTeklaConvert` gates on `TeklaObject.type` (`Beam`/`PolyBeam`/`ContourPlate` only — everything else returns `null`, falling to `DirectShape`), then resolves `class` via `TeklaClassCategoryResolver`:

   | Tekla `class` | Category | Converter |
   |---|---|---|
   | `7` (steel) / `13` (concrete) | `OST_StructuralColumns` | `ColumnToHostConverter` |
   | `8` | `OST_StructuralFoundation` | `FoundationToHostConverter` |
   | `1` (concrete panel) | `OST_Walls` | `WallToHostConverter` |
   | `11` (concrete slab) | `OST_Floors` | `FloorToHostConverter` |
   | anything else | `OST_StructuralFraming` | `BeamToHostConverter` (safe/conservative default) |

   A `TeklaObject` carrying `parentApplicationId` (a boolean-cut void) is checked *before* the type gate → `OpeningToHostConverter`, since the cutter's own type can itself be `"Beam"`.
7. **Profile mapping** — `TeklaProfileMappingDialog` prompts once for unresolved Tekla profiles.
8. **Identity closes the loop** — the object still carries the *original* Revit origin id (Tekla only added its own stamp alongside), so Revit's existing-element indexes recognize it and update in place.

## C.5 Workflow: Tekla → Speckle → Revit → Speckle → Tekla

The mirror image, for Tekla-authored elements.

**Hop 1 — Tekla → Speckle → Revit.** Tekla sends `TeklaObject`s (§B.5); Revit receives with `NativeTekla` using the dispatch table in §C.4 step 6 (a whole `Grid` object is handled separately via `TeklaGridSystemToHostConverter`, fanning out into N native `DB.Grid`s); `TeklaProfileMappingDialog` prompts if needed; the new Revit element is stamped with `OriginApplicationIdSchema` = the Tekla part's GUID.

**Hop 2 — Revit → Speckle → Tekla.** Revit sends the elements back as `RevitObject`s — `RevitOutgoingApplicationIdResolver` re-emits the *original Tekla-origin* `applicationId`, not the Revit element's own `UniqueId`; Tekla receives using the dispatch table in §C.4 step 2; `ConversionMappingDialog` prompts for anything not already covered by a saved default; `TeklaExistingBeamIndex` looks the incoming `applicationId` up as a *stamped* origin id (not a native GUID, since this copy's true origin is the Hop-1 Tekla part), finds the original part, and updates it in place.

## C.6 Object coverage across an interop hop

Only object kinds with an explicit entry in both dispatch tables (§C.4 step 2 / step 6) survive a cross-app hop as a native element:

| Kind | Revit → Tekla | Tekla → Revit |
|---|---|---|
| Beam / Column | ✅ `RevitColumnBeamToTeklaBeamConverter` | ✅ via `class` resolution |
| Wall | ✅ `RevitWallToTeklaBeamConverter` | ✅ `class=1` → `WallToHostConverter` |
| Floor / Slab | ✅ `RevitFloorToContourPlateConverter` | ✅ `class=11` → `FloorToHostConverter` |
| Foundation | ✅ `RevitFoundationToTeklaConverter` | ✅ `class=8` → `FoundationToHostConverter` |
| Opening | ✅ `RevitOpeningToBooleanPartConverter` (boolean cut) | ✅ (`parentApplicationId` gate) → `OpeningToHostConverter` |
| Grid | ✅ `RevitGridsToTeklaGridsConverter` | ✅ `TeklaGridSystemToHostConverter` |
| Reinforcement (`SingleRebar`/`RebarGroup`/`RebarMesh`/`RebarSet`) | n/a (Revit never sends these) | ❌ not matched by the type gate → generic `DirectShape` fallback; loses all typed rebar data |
| Bolts, welds, fittings, cut planes, chamfers | n/a | ❌ same — `DirectShape` geometry only |
| `BentPlate`, `SpiralBeam`, `LoftedPlate`, `RadialGrid` | n/a | ❌ same — `DirectShape` geometry only |
| MEP, most family instances, roofs (Tekla direction) | ❌ no Tekla-side converter exists | n/a |

DirectShape (Revit) and `GeometricItemToHostConverter` (Tekla) are always the last-resort fallback: geometry stays visible, but as an opaque placeholder — a further round-trip in that same app can still update its position but never turns it into a real, typed element, and passing it on to the *other* app again just carries the display mesh, not structural data.

## C.7 Known limitations

- **One-way loss of Tekla-only concepts.** Reinforcement, bolts, welds, fittings, and Tekla's curved/exotic part types (`BentPlate`, `SpiralBeam`, `LoftedPlate`) have no Revit equivalent. A Tekla → Revit hop reduces them to anonymous `DirectShape` geometry; a subsequent Revit → Tekla hop cannot reconstitute them as their original typed Tekla objects.
- **Mapping dialogs require a human in the loop the first time** a given profile/material/family-type is encountered in either direction. Saved defaults remove this after the first mapping.
- **Foundations always prompt** on the Tekla → Revit hop, even for rectangular profiles, since a footing's profile string is a plan footprint, not a cross-section.
- **Categories with no converter on the receiving side** (MEP arriving into Tekla, Tekla's exotic part types arriving into Revit) never become native elements — placeholder/DirectShape geometry only.
- **Locale dependence is deliberately engineered around, not eliminated** — both sides use locale-independent keys (`builtInCategory` strings, Tekla `class` numbers) for dispatch, and geometry-derived sizing instead of parameter *names* — but a user's manual mapping-dialog choices remain locale-sensitive to whatever catalog names they pick.
