# Revit ↔ Tekla Structures — Workflows & Interoperability

This document describes the round-trip and cross-application workflows supported by the Speckle Revit and Tekla Structures connectors, and how the underlying conversion mechanics make them work. It complements [`Connectors/Tekla/FUNCTIONAL_DESCRIPTION.md`](Connectors/Tekla/FUNCTIONAL_DESCRIPTION.md), which covers the Tekla connector's internals in isolation; this document focuses on what happens *between* the two connectors.

Four workflows are in scope:

1. **Revit → Speckle → Revit** (native round-trip)
2. **Tekla → Speckle → Tekla** (native round-trip)
3. **Revit → Speckle → Tekla → Speckle → Revit** (cross-app round-trip, Revit-authored data)
4. **Tekla → Speckle → Revit → Speckle → Tekla** (cross-app round-trip, Tekla-authored data)

## 1. The shared foundation

Neither connector emits a common, strongly-typed BIM schema (there is no shared `Objects.BuiltElements.Beam`/`Wall` contract in active use). Instead, each connector emits its own **generic "Data" object**:

- Revit → `Speckle.Objects.Data.RevitObject` — built by `ElementTopLevelConverterToSpeckle` (`Converters/Revit/Speckle.Converters.RevitShared/ToSpeckle/TopLevel/RevitElementTopLevelConverterToSpeckle.cs`). Nearly every Revit category (walls, floors, beams, columns, foundations, roofs, grids, openings, MEP, family instances…) funnels through this single converter — there are no per-category `WallToSpeckleConverter`/`BeamToSpeckleConverter` classes. The category is stamped as a dynamic, locale-independent `builtInCategory` property (e.g. `"OST_Walls"`), alongside `family`/`type`, `properties` (instance/type parameters), `location` and `displayValue`.
- Tekla → `Speckle.Objects.Data.TeklaObject` — built by `ModelObjectToSpeckleConverter` (see §5 of the Tekla functional description). The dispatch keys are `type` (CLR class name, e.g. `"Beam"`, `"ContourPlate"`) and a `class` property (Tekla's numeric part-class, the only signal that distinguishes a column or footing from an ordinary beam, since Tekla's API models both as `TSM.Beam`).

Each connector's receive side understands **its own** Data object natively (round-trip) *and* **the other connector's** Data object (cross-app), by reading these same dispatch keys. This is what makes interoperability possible without either connector assembly referencing the other's types — both depend only on `Speckle.Objects.Data`.

### Identity preservation (what stops every hop from duplicating geometry)

Both connectors stamp an **origin application ID** onto every element the first time it is ever received, and reuse it on every subsequent send — so an element keeps the same identity no matter how many times it crosses the Revit↔Tekla↔Speckle boundary.

| | Revit | Tekla |
|---|---|---|
| Stamp mechanism | `OriginApplicationIdSchema` — a fixed-GUID ExtensibleStorage schema (`Converters/Revit/Speckle.Converters.RevitShared/Helpers/OriginApplicationIdSchema.cs`) storing one string field directly on the `Element` | `TeklaOriginIdentifier` — an equivalent user-defined attribute (UDA) on the `ModelObject` |
| Existing-element lookup | `RevitExistingBeamIndex` / `RevitExistingWallIndex` / `RevitExistingFloorIndex` / `RevitExistingOpeningIndex` (`Converters/Revit/Speckle.Converters.RevitShared/Helpers/`) — each indexes elements by both `Element.UniqueId` and the stamped origin id | `TeklaExistingBeamIndex` (`Converters/Tekla/Speckle.Converters.TeklaShared/ToHost/TeklaExistingBeamIndex.cs`) — indexes parts by both native Tekla GUID and the stamped origin id |
| Why two keys | A Revit-authored element's own `UniqueId` *is* its origin id on first send; only cross-app elements need the stamped fallback | A Tekla-authored beam's own GUID *is* its origin id (matches on the very first round-trip, no stamp needed yet); an element that was **received into Tekla from another app** needs the stamped id, since its native GUID differs from its true cross-app origin |
| Deletion tracking | `GetDeletionCandidates()` — previously Speckle-managed elements (i.e. carrying the stamp) absent from the current receive payload are deleted | same pattern — only elements that have round-tripped at least once are ever deletion candidates, so native, never-synced content is never at risk |

Send-side symmetry: `RevitOutgoingApplicationIdResolver` (Revit) re-emits the stamped origin id rather than the current `UniqueId`, so an element doesn't "reset" its identity on every send; Tekla's part converters do the equivalent by re-reading `TeklaOriginIdentifier` before falling back to the part's own GUID.

**Net effect:** in any of the four workflows below, resending/re-receiving the same model converges to one element per Speckle object — nothing duplicates, and elements deleted at the source are deleted downstream too.

### Profile / material mapping dialogs (cross-app hops only)

A native round-trip never needs a mapping dialog — the receiving app already knows its own family/type or Tekla profile. A cross-app hop does, because "profile"/"material" are free-text strings in one app's vocabulary that need a human decision to map into the other's catalog:

| Direction | Dialog | Behavior |
|---|---|---|
| Tekla → Revit | `TeklaProfileMappingDialog` (`Connectors/Revit/Speckle.Connectors.RevitShared/Operations/Receive/ProfileMapping/`) | Code-built WPF grid: one row per distinct `(category, Tekla profile)` pair that can't auto-resolve. Rectangular profiles (`"{h}*{w}"`) auto-synthesize a matching Revit family/type and are skipped; **foundations always get a row**, since a footing's profile string is a plan footprint, not a cross-section, with no reliable auto-sizing heuristic. Each row is an editable dropdown of loaded `FamilySymbol`s (no free text). Persisted at `%AppData%\Speckle\Revit\tekla-profile-mapping.json`; "save as default" makes future receives skip already-mapped rows. |
| Revit → Tekla | `ConversionMappingDialog` (`Connectors/Tekla/Speckle.Connector.TeklaShared/Operations/Receive/ConversionMapping/`) | Rows are the union of the sender's attached `ConversionTable` (see below) and a scan of the actually-received `RevitObject`s, so commits sent before the table existed still get prompted. Each row maps a Revit family/type (or structural material name) to a Tekla catalog profile/material string, pre-filled and live-validated against the Tekla catalog via `TeklaCatalogValidator`. Persisted via `RevitProfileMaterialMappingProvider`. |

Both dialogs are modal, shown at most once per receive (only if unresolved rows exist), and both let the user opt to persist their choices as the new default so recurring profiles/materials only need mapping once.

The **`ConversionTable`** referenced above is a lightweight inventory Revit attaches to the root object on every send (`ConversionTableUnpacker`, `Connectors/Revit/Speckle.Connectors.RevitShared/HostApp/ConversionTableUnpacker.cs`): distinct structural family/types (with cheap width/height-mm hints for rectangular sections) and distinct structural material names, scoped to categories Tekla can natively receive. It's best-effort — a send never fails because of it — and its purpose is purely to let the Tekla-side mapping dialog show useful suggestions before any objects are even walked.

---

## 2. Workflow: Revit → Speckle → Revit

The straightforward native round-trip.

1. **Send** — `ElementTopLevelConverterToSpeckle` converts each selected element to a `RevitObject`; `RevitOutgoingApplicationIdResolver` resolves a stable `applicationId` (origin id if previously received, else the element's own `UniqueId`).
2. **Receive** — `ReceiveMode` auto-resolves to `NativeRevit` (the model card's source application isn't Tekla) unless "Receive as Native Elements" is off, in which case everything becomes `DirectShape`. In `NativeRevit` mode, `RevitRootToHostConverter.TryNativeRevitConvert` dispatches on the `builtInCategory` property to `GridToHostConverter` / `BeamToHostConverter` / `ColumnToHostConverter` / `WallToHostConverter` / `FloorToHostConverter` / `FoundationToHostConverter` / `RoofToHostConverter`, plus `OpeningToHostConverter` for anything category-matching `"Opening"`.
3. **Identity** — the existing-element indexes (§1) match incoming objects to elements already in the model (by `UniqueId` or stamped origin id) and update them **in place** — reposition, re-type, or resize — instead of inserting duplicates. `FloorToHostConverter` specifically compares footprints to decide between a cheap in-place update and a delete-recreate, since `DB.Floor` has no simple boundary-edit API.
4. **Deletions** — elements previously baked by Speckle but absent from the current payload are deleted.
5. Anything without a category-specific converter (MEP, most family instances, etc.) falls through to `DirectShape`, Revit's generic geometry-only fallback.

No mapping dialog is involved — the receiving element already carries its real Revit family/type from the previous send.

## 3. Workflow: Tekla → Speckle → Tekla

The Tekla-side equivalent (full detail in the Tekla functional description, §4–§7; summarized here for symmetry).

1. **Send** — `ModelObjectToSpeckleConverter` walks the selection (and each part's sub-components — bolts, welds, cuts, chamfers, reinforcement) into a `TeklaObject` tree; `applicationId` is the Tekla GUID.
2. **Receive** — `TeklaRootToHostConverter` dispatches by `TeklaObject.type` to a dedicated part converter (`Beam`/`Column` → beam converter, plus `ContourPlate`, `PolyBeam`, `BentPlate`, `SpiralBeam`, `LoftedPlate`, `Grid`, `RadialGrid`), then a second pass attaches sub-components (bolt groups, welds, fittings, boolean cuts, reinforcement) by looking up their parent in `TeklaReceiveCache`, then a third pass groups parts sharing an `assembly_id` into Tekla assemblies.
3. **Identity** — `TeklaExistingBeamIndex`-style matching by native GUID (a Tekla-authored part's GUID is its own origin id, so this always matches from the first round-trip).
4. Reinforcement, bolts, welds and other sub-components are Tekla-native concepts that only exist in this workflow — they have no Revit representation (see §6).

No mapping dialog is involved — Tekla already knows its own profile catalog.

## 4. Workflow: Revit → Speckle → Tekla → Speckle → Revit

Revit-authored elements travel to Tekla and back.

**Hop 1 — Revit → Speckle → Tekla.**
1. Revit sends `RevitObject`s exactly as in §2 (step 1).
2. Tekla receives: `TeklaRootToHostConverter.Convert` sees a `Base` that is a `RevitObject` (not a `TeklaObject`) and dispatches on `builtInCategory`:

   | `builtInCategory` | Converter | Notes |
   |---|---|---|
   | `OST_Walls` | `RevitWallToTeklaBeamConverter` | |
   | `OST_Floors` | `RevitFloorToContourPlateConverter` | |
   | `OST_StructuralColumns`, `OST_StructuralFraming` | `RevitColumnBeamToTeklaBeamConverter` | Straight (line-placed) elements become a `TSM.Beam`; point-placed columns synthesize a vertical segment from the column's height; curved axes become a `TSM.PolyBeam` following the true curve. |
   | `OST_StructuralFoundation` | `RevitFoundationToTeklaConverter` | Point/line-placed footings become sized concrete beams (dimensions read from the element's `displayValue` bounding box — deliberately not from family parameter names, which are locale-dependent, e.g. German "Breite"/"Höhe"); slab-like foundations delegate to the floor converter. |
   | *(contains)* `"Opening"` | `RevitOpeningToBooleanPartConverter` | Converts to a `TSM.BooleanPart` cut on the already-converted host part (resolved via `parentApplicationId` + `TeklaReceiveCache`), not a standalone part. |
   | `OST_StructuralFramingSystem` | *(skipped)* | The sketch container has no independent geometry — its member beams arrive separately as ordinary `OST_StructuralFraming` objects. |
   | anything else | *(unhandled → generic/DirectShape fallback further down the dispatch chain)* | |

3. **Profile/material mapping** — `ConversionMappingDialog` prompts (once) for any unresolved Revit family/type or structural material, then the mapped Tekla catalog profile/material is applied.
4. **Identity on this hop** — since these elements have never been in Tekla before, `TeklaExistingBeamIndex` finds no match by GUID; a fresh part is created and immediately stamped with `TeklaOriginIdentifier` = the Revit element's origin id (its Revit `applicationId`/stamped id).

**Hop 2 — Tekla → Speckle → Revit.**
5. Tekla sends the (possibly user-edited) parts back as `TeklaObject`s, carrying `applicationId` = the Tekla GUID, but the object's captured `class`/`type` now reflect Tekla's own vocabulary (e.g. class `7`/`13` for a column, `8` for a foundation).
6. Revit receives with `ReceiveMode.NativeTekla` (auto-detected since the model card's source app is Tekla). `RevitRootToHostConverter.TryNativeTeklaConvert` gates on `TeklaObject.type` (`Beam`/`PolyBeam`/`ContourPlate` only — everything else, e.g. `BentPlate`/`SpiralBeam`/`LoftedPlate`/`RadialGrid`/reinforcement/bolts/welds, returns `null` here), then resolves `class` via `TeklaClassCategoryResolver` to a `BuiltInCategory`:

   | Tekla `class` | Resolved category | Revit converter |
   |---|---|---|
   | `7` (steel) / `13` (concrete) | `OST_StructuralColumns` | `ColumnToHostConverter` |
   | `8` | `OST_StructuralFoundation` | `FoundationToHostConverter` |
   | `1` (concrete panel) | `OST_Walls` | `WallToHostConverter` |
   | `11` (concrete slab) | `OST_Floors` | `FloorToHostConverter` |
   | anything else | `OST_StructuralFraming` | `BeamToHostConverter` (safe/conservative default — a fabricator's custom class scheme is possible) |

   A `TeklaObject` carrying a `parentApplicationId` (a boolean-cut void) is checked *before* the type gate and routed to `OpeningToHostConverter` instead, since the cutter's own Tekla type can itself be `"Beam"`.
7. **Profile mapping** — `TeklaProfileMappingDialog` prompts (once) for any Tekla profile that didn't already auto-resolve, mapping it to a Revit `FamilySymbol`.
8. **Identity closes the loop** — because the object being received still carries the *original* Revit origin id (Tekla never overwrote it, only added its own `TeklaOriginIdentifier` stamp alongside), Revit's existing-element indexes recognize it as the same element that was sent away in Hop 1, and update it in place rather than creating a duplicate.

## 5. Workflow: Tekla → Speckle → Revit → Speckle → Tekla

The mirror image, for Tekla-authored elements.

**Hop 1 — Tekla → Speckle → Revit.**
1. Tekla sends `TeklaObject`s as in §3 (step 1).
2. Revit receives with `ReceiveMode.NativeTekla`, using the same `TryNativeTeklaConvert` dispatch table shown in §4 step 6 (type gate → `TeklaClassCategoryResolver` → Column/Foundation/Wall/Floor/Beam converter). A whole Tekla `Grid` object is handled separately (`TeklaGridSystemToHostConverter`, one `TeklaObject` fans out into N native `DB.Grid`s).
3. **Profile mapping** — `TeklaProfileMappingDialog` as in §4 step 7.
4. **Identity** — first arrival, no existing match; the new Revit element is stamped with `OriginApplicationIdSchema` = the Tekla part's GUID.

**Hop 2 — Revit → Speckle → Tekla.**
5. Revit sends the (possibly user-edited) elements back as `RevitObject`s. `RevitOutgoingApplicationIdResolver` re-emits the *original Tekla-origin* `applicationId` (the stamped one), not the Revit element's own `UniqueId`.
6. Tekla receives with the `RevitObject` dispatch table from §4 step 2 (`builtInCategory` → wall/floor/beam-or-column/foundation/opening converter).
7. **Profile/material mapping** — `ConversionMappingDialog` as in §4 step 3, for anything not already covered by a saved default.
8. **Identity closes the loop** — `TeklaExistingBeamIndex` looks the incoming `applicationId` up as a *stamped origin id* (not the part's native GUID, since this object's true origin is a Tekla part from Hop 1 whose GUID this Revit-round-tripped copy no longer carries directly — it carries the stamp instead), finds the original part, and updates it in place.

## 6. Object coverage across an interop hop

Only object kinds with an explicit entry in both dispatch tables (§4 step 2 / §4 step 6) survive a cross-app hop as a native element. Everything else has no bidirectional path:

| Kind | Revit → Tekla | Tekla → Revit |
|---|---|---|
| Beam / Column | ✅ `RevitColumnBeamToTeklaBeamConverter` | ✅ via `class` resolution → Beam/Column converter |
| Wall | ✅ `RevitWallToTeklaBeamConverter` | ✅ `class=1` → `WallToHostConverter` |
| Floor / Slab | ✅ `RevitFloorToContourPlateConverter` | ✅ `class=11` → `FloorToHostConverter` |
| Foundation | ✅ `RevitFoundationToTeklaConverter` | ✅ `class=8` → `FoundationToHostConverter` |
| Opening | ✅ `RevitOpeningToBooleanPartConverter` (boolean cut) | ✅ (`parentApplicationId` gate) → `OpeningToHostConverter` |
| Grid | ✅ `RevitGridsToTeklaGridsConverter` | ✅ `TeklaGridSystemToHostConverter` |
| Reinforcement (`SingleRebar`/`RebarGroup`/`RebarMesh`/`RebarSet`) | n/a (Revit never sends these) | ❌ not matched by `TryNativeTeklaConvert`'s type gate → falls to Revit's generic `DirectShape` fallback; loses all typed rebar data |
| Bolts, welds, fittings, cut planes, chamfers | n/a | ❌ same — `DirectShape` geometry only |
| `BentPlate`, `SpiralBeam`, `LoftedPlate`, `RadialGrid` | n/a (Revit has no source shape for these) | ❌ same — `DirectShape` geometry only |
| MEP, most family instances, roofs (Tekla direction) | ❌ no Tekla-side converter exists | n/a |

DirectShape (Revit) and the generic `GeometricItemToHostConverter` placeholder (Tekla) are always the last-resort fallback: geometry stays visible, but it's an opaque placeholder — a further round-trip through that same app can still update its position but never turns it into a real, typed element, and passing it on to the *other* app again just carries the display mesh, not structural data.

## 7. Known limitations

- **One-way loss of Tekla-only concepts.** Reinforcement, bolts, welds, fittings, and Tekla's curved/exotic part types (`BentPlate`, `SpiralBeam`, `LoftedPlate`) have no Revit equivalent. A Tekla → Revit hop reduces them to anonymous `DirectShape` geometry; a subsequent Revit → Tekla hop cannot reconstitute them as their original typed Tekla objects.
- **Mapping dialogs require a human in the loop the first time** a given profile/material/family-type is encountered in either direction. Saved defaults remove this after the first mapping, but a completely new section or material always needs one manual decision per direction.
- **Foundations always prompt** on the Tekla → Revit hop, even for rectangular profiles, because a footing's captured profile string is a plan footprint (not a cross-section) and there's no reliable universal parameter-name heuristic to auto-size a footing family/type.
- **Categories with no converter on the receiving side** (e.g. MEP elements arriving from Revit into Tekla, or Tekla's exotic part types arriving into Revit) never become native elements on that side — only placeholder/DirectShape geometry.
- **Locale dependence is deliberately engineered around**, not eliminated: both sides use locale-independent keys (`builtInCategory` strings, Tekla `class` numbers) for dispatch, and geometry-derived sizing (bounding boxes) instead of parameter *names* (e.g. German "Breite"/"Höhe") wherever a Revit family's own parameters are read across the interop boundary — but a user's own manual mapping-dialog choices are obviously still locale-sensitive to whatever catalog names they pick.
