# Tekla Structures Connector — Functional Description

This document describes the functional behavior of the Speckle connector for Tekla Structures (currently shipping for Tekla 2025, sharing the bulk of its implementation with the 2023/2024 variants).

## 1. Architecture overview

The connector follows a **shared-project pattern**: thin, version-specific projects compile a large shared codebase against a particular Tekla SDK version.

- `Connectors/Tekla/Speckle.Connector.Tekla2025` and `Converters/Tekla/Speckle.Converter.Tekla2025` — version-specific shells. They reference the Tekla 2025 assemblies (`Tekla.Structures.Model`, `.Dialog`, `.Drawing`, `.Plugins`) and compile with the `TEKLA2025` conditional symbol. Both target `net48`/x64, since Tekla loads plugins as in-process .NET Framework assemblies.
- `Connectors/Tekla/Speckle.Connector.TeklaShared` — connector bindings, send/receive operations, host-app services, settings, DI registration.
- `Converters/Tekla/Speckle.Converters.TeklaShared` — ToSpeckle and ToHost conversion logic, geometry/property extraction helpers, conversion settings, DI registration.

The connector is built and loaded as a **Tekla plugin** (`.dll`), and the Speckle UI is hosted inside Tekla's window manager via a WPF `ElementHost`.

## 2. Plugin host & UI integration

`SpeckleTeklaPanelHost` is the plugin entry point (subclass of `PluginFormBase`):

- Tekla invokes plugin forms twice on first activation; the host closes itself on the first invocation and performs full setup on the second (`InitializeInstance`).
- It connects to the active `Tekla.Structures.Model.Model`, builds the DI container (`AddTekla()` + `AddTeklaConverters()`), instantiates the `DUI3ControlWebView`, and embeds it via a Windows Forms `ElementHost` parented to Tekla's main window frame.
- A static `IsInitialized` flag prevents duplicate panels; closing resets it so the panel can be reopened.

## 3. Connector bindings

The shared project registers the following `IBinding` implementations:

| Binding | Responsibility |
|---|---|
| `TeklaBasicConnectorBinding` | Reports source application name/version ("Tekla"/"2025"), document info (path, name, hash), and implements **highlight/zoom**: resolves object GUIDs to `ModelObject`s, selects them, computes a bounding box, and zooms the active view to it. |
| `TeklaSendBinding` | Orchestrates the send pipeline (see §4); exposes send filters and send settings; subscribes to Tekla's `ModelObjectChanged` event to track changed objects, expire model cards, and evict stale cache entries. |
| `TeklaReceiveBinding` | Orchestrates the receive pipeline (see §5); resolves the receive mode and exposes the receive-mode setting; failures are logged and swallowed (returns `null` rather than throwing). |
| `TeklaSelectionBinding` | Mirrors Tekla's live selection to the UI: queries `ModelObjectSelector.GetSelectedObjects()`, filters out construction objects with empty GUIDs, and pushes a `setSelection` event with the GUIDs and a human-readable summary whenever Tekla's `SelectionChange` event fires. |

Standard DUI bindings (`TestBinding`, `ConfigBinding`, `AccountBinding`) are also registered through `Services.AddConnectors()`/`AddDUI<…>()`.

## 4. Send pipeline

**Selection.** The user selects objects in the Tekla model; `TeklaSelectionFilter` returns the selected GUIDs, which `TeklaSendBinding.Send()` resolves back to `ModelObject`s via `model.SelectModelObject(new Identifier(guid))`. There is no automatic traversal beyond the selection — children are picked up later during conversion.

**Conversion.** `TeklaRootObjectBuilder` (batch mode) and `TeklaContinuousTraversalBuilder` (streaming mode, feeds a `SendPipeline` for incremental packfile upload) both:

1. Create a root `Collection` named after the model.
2. For each selected object, compute a deterministic `applicationId` (GUID-based), check the **send conversion cache** (scoped by project + applicationId), and on a miss call `TeklaRootToSpeckleConverter.Convert()`.
3. Place converted objects into a type-based collection hierarchy via `SendCollectionManager` (e.g. all `Beam`s grouped together, all `BoltGroup`s together).
4. Capture render-material information via `TeklaMaterialUnpacker`, producing one `RenderMaterialProxy` per distinct ARGB color found via `ModelObjectVisualization.GetRepresentation()`.
5. Conversion failures are logged and additionally written to `%TEMP%\speckle_tekla_send_error.txt` for diagnostics; they don't stop the overall send.

**Cache invalidation.** The send conversion cache is invalidated when: (a) Tekla raises `ModelObjectChanged` for a cached object, or (b) the user toggles the "Send rebars as solid" setting (handled by `ToSpeckleSettingsManager`, which evicts only the affected rebar objects).

## 5. Conversion to Speckle (ToSpeckle)

Every converted Tekla `ModelObject` becomes a `Speckle.Objects.Data.TeklaObject` (the connector previously had its own `TeklaObject` type with PascalCase property names that the Speckle viewer couldn't read; it now uses the SDK's lower-camelCase version).

`ModelObjectToSpeckleConverter` builds each `TeklaObject` from:

- **type** — the CLR class name (e.g. `Beam`, `BoltArray`, `RebarGroup`).
- **name** — `Part.Name` / `Reinforcement.Name`, falling back to the type name.
- **elements** — recursively converted children (`GetSupportedChildren()`, which filters out `ControlPoint`s), forming a nested `TeklaObject` tree (e.g. a `Beam` contains its bolts, welds, cuts, chamfers, boolean operations, and reinforcement as child elements).
- **properties** — a flat dictionary produced by `ClassPropertyExtractor`, covering: profile/material/class/finish, position (depth/plane/rotation + offsets), numbering (part & assembly prefixes/start numbers), phase, `assembly_id`/`is_main_part`, plus type-specific data for bolt groups (pattern, hole/thread/tolerance data, and the connection: `mainPartId` for `PartToBeBolted`, `partToBoltToId` for the required primary secondary part `PartToBoltTo`, and `secondaryPartIds` for any further parts in `OtherPartsToBolt`), reinforcement (grade/size/class, hook types, leg faces, guidelines — see below for `RebarMesh`/`RebarSet` specifics), sub-components (fitting planes, chamfer geometry, boolean operation data, weld parameters incl. `main_id`/`secondary_id` for `MainObject`/`SecondaryObject`), and grids (coordinate strings, labels, extensions).
  - **`RebarMesh`** — captures the full mesh definition needed to recreate it: `meshType` (`RECTANGULAR_MESH`/`POLYGON_MESH`/`BENT_MESH`, fixed at creation time), `name`/`grade`/`class`/`catalogName`, `longitudinalSize`/`crossSize`, `longitudinalSpacingMethod` plus `longitudinalDistances`/`crossDistances`, the four overhang values, `crossBarLocation`, `cutByFatherPartCuts`, the plane/point offset data (`fromPlaneOffset` family, `onPlaneOffsets`, start/end point offset type & value), and either `startPoint`/`endPoint`/`length`/`width` (rectangular meshes) or a `Polygon` outline carried via `location` (polygon/bent meshes).
  - **`RebarSet`** — `father_id`, `layer_order_number`, `RebarProperties` (`rebar_size/grade/name/class/bending_radius`), `leg_faces` (contours with `additional_offset`/`layer_order_number`/`reversed`), and `guidelines` (curves with `follow_edges` plus the full `RebarSpacing` definition: `spacing_type`, bar count, target/exact distances, start/end offsets and their automatic flags, and `spacing_exact_elements` for explicit exact-spacing lists).
- **location** — the primary reference geometry, type-dependent: a `Line` for beams/spiral beams, a `Polyline` for plates and reinforcement contours (with chamfer metadata attached as a dynamic property), a `Line` along a fitting plane's local X-axis, the first leg face's contour for `RebarSet` (representative outline — a set has no single defining curve), or `null` where no simple representation exists (e.g. `BentPlate`, grids).
- **displayValue** — the visualization geometry, also type-dependent: a `Mesh` derived from the object's solid for parts/bolt groups/rebar meshes; for reinforcement (and for `RebarSet`, via its generated `GetReinforcements()` bars, since a set has no solid/geometry of its own) either a solid-derived `Mesh` or centerline `Line`/`Arc` segments depending on the **"Send rebars as solid"** setting; multiple `Line`s for grids; nothing for unsupported types.
- **applicationId** / **units** — the Tekla GUID and the project's Speckle unit string.

## 6. Receive pipeline

`TeklaReceiveBinding` resolves the active **receive mode** (Native vs Generic) via `TeklaToHostSettingsManager` — explicit per-card UI setting first, then auto-detection from the source application (currently always resolves to Native, with Generic available as a manual override/fallback).

`TeklaHostObjectBuilder` then runs a **three-pass build**:

1. **Main parts.** The incoming object tree is flattened to atomic `TeklaObject`s (recursing through `Collection`s, stopping at `TeklaObject` leaves). Known sub-component types (bolt groups, welds, seams, fittings, boolean parts, cut planes, edge chamfers, and all reinforcement variants) are skipped here. Each remaining "main part" is converted via `TeklaRootToHostConverter`, its `assembly_id`/`is_main_part` flags captured, and the resulting `ModelObject` registered in `TeklaReceiveCache`.
2. **Sub-components.** The original tree is walked again; for each `TeklaObject` with child elements, the parent `ModelObject` is looked up in the cache (by Speckle id or original `applicationId`/`father_id`), and `SubComponentToHostConverter.ConvertAndAttach()` builds and attaches the appropriate sub-object (bolt group, weld, fitting, boolean cut, cut plane, edge chamfer, or reinforcement). Cache misses (parent not yet baked) are logged.
3. **Assemblies.** Parts sharing the same `assembly_id` are grouped; for groups with more than one member, the secondary parts are added to the main part's `Assembly` and the assembly is persisted via `assembly.Modify()`.

Finally `model.CommitChanges()` applies all inserts/modifications and the result (baked object IDs + per-object conversion results) is returned.

`TeklaReceiveCache` is a per-operation, dual-keyed (Speckle id **and** original Tekla `applicationId`) map of converted objects, allowing both fresh receives and "replace existing" receives (where sub-components reference their parent by the original GUID stored in `father_id`) to resolve parents correctly.

## 7. Conversion to Tekla (ToHost)

`TeklaRootToHostConverter` is the dispatcher:

- For `TeklaObject`s, it switches on `type` and routes to a dedicated converter — `Beam`/`Column` → `BeamToHostConverter`, plus dedicated converters for `ContourPlate`, `PolyBeam`, `BentPlate`, `SpiralBeam`, `LoftedPlate`, `Grid`, and `RadialGrid`. Unsupported types throw a `ConversionException`. Successful conversions are registered in the receive cache.
- For `BuiltElements.Beam`/`Column` (objects coming from other host applications, e.g. Revit), **Native** mode tries `BuiltElementBeamToHostConverter`/`BuiltElementColumnToHostConverter` first (extracting `baseLine`, `profile`, `material` with sensible defaults such as `HEA200`/`S235JR`), falling back to the generic converter on failure; **Generic** mode uses the generic converter directly.
- `GeometricItemToHostConverter` is the last-resort fallback: it builds a placeholder `TSM.Beam` ("Generic Placeholder", profile `HEA200`) carrying the object's display meshes, so geometry from unsupported sources is still visible in the model (full Brep/Mesh import is noted as a future improvement).

Common part properties (profile, material, class, finish, position, numbering, phase, beam end-point offsets, and user-defined attributes/UDAs) are applied uniformly by `TeklaPartPropertyApplicator`, which every part-level converter calls before `Insert()`.

`SubComponentToHostConverter` builds and attaches the various sub-object kinds onto an already-created parent `ModelObject`:

- **Bolt groups** (array/circle/XY patterns) — reconstruct the full Tekla bolting model: `PartToBeBolted` (main part) is resolved from the cached `mainPartId`, falling back to the Speckle-tree parent; `PartToBoltTo` (Tekla's required primary secondary part — may legitimately equal the main part for single-ply connections such as anchor bolts/shear studs) is resolved from `partToBoltToId`; any remaining parts in `secondaryPartIds` are attached via `AddOtherPartToBolt` for multi-ply connections.
- **Welds** — resolve `MainObject`/`SecondaryObject` from the cached `main_id`/`secondary_id` (falling back to the tree parent for the main object); the weld is skipped if either side can't be resolved, since Tekla requires both ends to exist.
- **Fittings**, **boolean parts/cuts**, **cut planes**, **edge chamfers**, and **reinforcement**:
  - `SingleRebar`/`RebarGroup` — polygon reconstruction from the `location` polyline.
  - `RebarMesh` — full reconstruction from the captured mesh data: `MeshType` is set first (Tekla forbids changing it later), then name/grade/class/catalog/sizes/spacing/distances/overhangs/offsets/cross-bar location, and finally the geometry — `StartPoint`/`EndPoint`/`Length`/`Width` for `RECTANGULAR_MESH`, or a `Polygon` rebuilt from the `location` polyline for `POLYGON_MESH`/`BENT_MESH`.
  - `RebarSet` — rebuilds `RebarProperties`, `LegFaces` (with `Contour`s), and `Guidelines`, including a correctly-constructed `RebarSpacing` per guideline: built via `RebarSpacing.Create(...)` using the captured `spacing_type` to pick the right factory overload (number-of-bars, exact-spacings list, or single-distance types), since `RebarSpacing` cannot be validly produced by default-constructing it and setting properties afterwards.

## 8. Settings

| Setting | Scope | Purpose |
|---|---|---|
| **Send rebars as solid** (`SendRebarsAsSolidSetting`, boolean) | Send | Controls whether reinforcement is exported as a solid-derived `Mesh` (`true`, faster/coarser) or as centerline `Line`/`Arc` geometry (`false`, slower/more accurate). Toggling it evicts affected entries from the send conversion cache (`ToSpeckleSettingsManager`). |
| **Receive mode** (`ReceiveModeSetting`, enum: Native/Generic) | Receive | Selects whether cross-application `BuiltElements` are converted via dedicated Tekla-aware converters (Native, with fallback to generic on failure) or always routed through the generic placeholder converter (Generic). `TeklaToHostSettingsManager` auto-resolves to Native unless the user overrides it. |

`TeklaConversionSettings` (created by `TeklaConversionSettingsFactory`) bundles the active `Model`, the rebar/receive-mode choices, and the Speckle unit string derived from Tekla's current display unit (`Tekla.Structures.Datatype.Distance.CurrentUnitType`) — Tekla's internal model is always in millimeters, so the converter maps to the user's display units on send.

## 9. Tekla object types supported

| Tekla type | Speckle representation | Notes |
|---|---|---|
| `Beam`, `Column` | `TeklaObject` (`type="Beam"`/`"Column"`) | Line geometry + profile/material; Tekla treats columns as beams |
| `ContourPlate` | `TeklaObject` (`type="ContourPlate"`) | Polyline contour incl. chamfer metadata |
| `PolyBeam` | `TeklaObject` (`type="PolyBeam"`) | Multi-segment beam from contour |
| `BentPlate` | `TeklaObject` (`type="BentPlate"`) | Geometry only via `ConnectiveGeometry`; no extractable `location` |
| `SpiralBeam` | `TeklaObject` (`type="SpiralBeam"`) | Spiral parameters (rise, rotation/twist angles, axis) |
| `LoftedPlate` | `TeklaObject` (`type="LoftedPlate"`) | Multiple base curves + face type |
| `Grid`, `RadialGrid` | `TeklaObject` (`type="Grid"`/`"RadialGrid"`) | Coordinate strings, labels, extensions; displayed as line sets |
| `BoltArray`/`BoltCircle`/`BoltXY` | sub-component | Pattern-specific coordinates, hole/thread data; full connection captured/restored via `mainPartId` (`PartToBeBolted`), `partToBoltToId` (`PartToBoltTo`) and `secondaryPartIds` (`OtherPartsToBolt`), each resolved through the receive cache |
| `Weld`, `Seam` | sub-component | References `MainObject`/`SecondaryObject` via `main_id`/`secondary_id` cache lookup |
| `Fitting`, `CutPlane`, `EdgeChamfer`, `BooleanPart` | sub-component | Plane- or operation-based geometry attached to a parent part |
| `SingleRebar`, `RebarGroup` | sub-component (reinforcement) | Polygon-based geometry; grade/size/class/hook data; attached via `father_id` |
| `RebarMesh` | sub-component (reinforcement) | Full mesh definition (type, sizes, spacing, distances, overhangs, offsets) plus rectangular start/end/length/width or polygon outline; `MeshType` fixed at creation; attached via `father_id` |
| `RebarSet` | sub-component (reinforcement) | `RebarProperties`, leg-face contours and guidelines incl. a properly-factory-built `RebarSpacing`; `location` derived from the first leg face's contour, `displayValue` from its generated reinforcements (`GetReinforcements()`) since the set itself has no solid; attached via `father_id` |
| `BuiltElements.Beam`/`Column` (cross-app) | `Base` | Converted via `BuiltElement*ToHostConverter` (Native) or generic placeholder (Generic) |
| Anything else unsupported | `Base` | `GeometricItemToHostConverter` placeholder beam carrying the original display mesh |

## 10. Dependency injection summary

`ServiceRegistration.AddTekla()` (connector) registers: the browser bridge, idle manager, DUI framework wiring, all bindings, Tekla API singletons (`Model`, `Events`, `ModelObjectSelector`), the send pipeline (`TeklaSelectionFilter`, `SendConversionCache`, `SendCollectionManager`, `TeklaRootObjectBuilder`/`TeklaContinuousTraversalBuilder`, `SendOperation`), the receive pipeline (`TeklaHostObjectBuilder`, `TeklaMaterialUnpacker`), settings managers, the converter settings store, default graph traversal, and progress management; it also auto-registers all `IToSpeckleTopLevelConverter` implementations in the assembly.

`ServiceRegistration.AddTeklaConverters()` (converters) registers: the extraction helpers (`DisplayValueExtractor`, `ClassPropertyExtractor`, `ReportPropertyExtractor`, `UserDefinedAttributesExtractor`, `PropertiesExtractor`, `LocationExtractor`), the root ToSpeckle/ToHost converters, all type-specific ToHost converters, the receive cache, sub-component converter, generic/built-element converters, the unit converter, and the converter settings store; it also auto-registers remaining converters by interface matching.
