using Microsoft.Extensions.Logging;
using Speckle.Connectors.Common.Operations;
using Speckle.Converters.IfcShared.BlobRetrieval;
using Speckle.Converters.IfcShared.Extraction;
using Speckle.Converters.IfcShared.Geometry;
using Speckle.Converters.IfcShared.StepParsing;
using Speckle.Objects.Data;
using Speckle.Sdk.Models;
using Speckle.Sdk.Models.Collections;
using SOG = Speckle.Objects.Geometry;

namespace Speckle.Converters.IfcShared.Enrichment;

/// <summary>
/// The real (non-no-op) <see cref="IReceivedObjectEnricher"/> for IFC-sourced versions: resolves and
/// downloads the original <c>.ifc</c> blob, re-parses it, and for each already-received
/// <see cref="DataObject"/> whose <c>applicationId</c> (the IFC <c>GlobalId</c>) matches an entity in
/// the file, enriches it with <c>builtInCategory</c>/<c>location</c>/<c>profile</c> so it routes
/// through the existing native converters (see <c>RevitRootToHostConverter.TryNativeRevitConvert</c>)
/// instead of falling to <c>DirectShape</c>. <c>IfcOpeningElement</c>s are the one exception - see
/// <c>TryBuildOpeningDataObject</c>'s remarks - since none exist as their own received object at all,
/// this synthesizes new ones and injects them directly into the received tree.
/// </summary>
/// <remarks>
/// Scope: <c>IfcColumn</c>, <c>IfcBeam</c>/<c>IfcMember</c>, <c>IfcWall</c>/<c>IfcWallStandardCase</c>,
/// <c>IfcSlab</c> (<c>.FLOOR.</c> only), and <c>IfcOpeningElement</c> (rectangular only - see the
/// plan's Phase 2-4 scoping). Soft-fails at every step - a version that isn't IFC-sourced, a file that
/// fails to download, a file that fails to parse as STEP, or any single element that doesn't have the
/// data this needs, all fall through to the existing, unmodified <c>DirectShape</c> path exactly as
/// before this feature existed. This method must never throw and never block a receive.
/// </remarks>
public sealed class RevitNativeSchemaEnricher(
  IIfcSourceResolver sourceResolver,
  IIfcBlobDownloader blobDownloader,
  IIfcGlobalIdScanner globalIdScanner,
  ILogger<RevitNativeSchemaEnricher> logger
) : IReceivedObjectEnricher
{
  public async Task<Base> Enrich(Base rootObject, ReceiveInfo receiveInfo, CancellationToken cancellationToken)
  {
    string? blobId = await sourceResolver.TryResolveBlobId(receiveInfo, cancellationToken).ConfigureAwait(false);
    if (blobId is null)
    {
      // Not an IFC-sourced version (or the lookup failed) - IfcSourceResolver already logged why.
      return rootObject;
    }

    string? filePath = await blobDownloader.TryDownload(receiveInfo, blobId, cancellationToken).ConfigureAwait(false);
    if (filePath is null)
    {
      return rootObject;
    }

    try
    {
      EnrichFromFile(rootObject, filePath);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      // Soft-fail by design: a corrupt download, a non-STEP file matched by mistake, or any parsing
      // surprise must never block the receive - every object simply keeps falling to DirectShape.
      logger.LogWarning(
        ex,
        "RevitNativeSchemaEnricher: failed to process downloaded file {FilePath} - all objects fall back to DirectShape.",
        filePath
      );
    }
    finally
    {
      TryDeleteTempFile(filePath);
    }

    return rootObject;
  }

  private void EnrichFromFile(Base rootObject, string filePath)
  {
    var globalIdToExpressId = globalIdScanner.ScanGlobalIds(filePath);

    using var doc = new StepDocument(filePath);
    var graph = StepGraph.Create(doc);

    // Resolved ONCE per file, applied by every TryEnrichXxx method below to its own final world
    // values (never threaded into the STEP-parsing/placement-composition extractors themselves - see
    // IfcUnitResolver's remarks for why this is safe). Defaults to 1.0 (this feature's original
    // hardcoded millimetre assumption) for a file with no readable length unit.
    double lengthScaleToMm = IfcUnitResolver.ResolveLengthScaleToMillimeters(graph);
    if (lengthScaleToMm != 1.0)
    {
      logger.LogInformation(
        "RevitNativeSchemaEnricher: resolved length unit scale factor {LengthScaleToMm} (raw file units -> millimetres).",
        lengthScaleToMm
      );
    }

    // One-time reverse scans (see IfcMaterialThicknessExtractor/IfcOpeningHostExtractor's remarks) -
    // reused for every wall/floor/opening below rather than re-scanning the whole file per element.
    var elementToMaterial = IfcMaterialThicknessExtractor.BuildElementToMaterialIndex(graph);
    var hostToOpenings = new Dictionary<uint, List<uint>>();
    foreach (var pair in IfcOpeningHostExtractor.BuildOpeningToHostIndex(graph))
    {
      if (!hostToOpenings.TryGetValue(pair.Value, out var openings))
      {
        openings = [];
        hostToOpenings[pair.Value] = openings;
      }
      openings.Add(pair.Key);
    }

    // Per-category candidate/enriched counts (candidate = ifcType matches, regardless of whether
    // enrichment then succeeded) - deliberately more granular than a single aggregate count, so a
    // "why did category X not convert" report can be diagnosed from the log alone: a candidate count
    // of 0 means the GlobalId/ifcType never matched at all (upstream data issue), while
    // candidates > enriched means the enrichment logic itself is bailing out (axis/placement/profile
    // extraction failing) - two very different problems that look identical from the outside
    // (everything just falls to DirectShape either way).
    var candidateCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var synthesizedOpenings = new List<DataObject>();
    var synthesizedGrids = new List<DataObject>();
    int enrichedCount = 0;
    int wallEnrichedCount = 0;
    int floorEnrichedCount = 0;
    int foundationSlabEnrichedCount = 0;
    int gridEnrichedCount = 0;
    int elementAssemblySkippedCount = 0;
    foreach (var candidate in TraverseAll(rootObject))
    {
      if (candidate is not DataObject dataObject)
      {
        continue;
      }

      if (dataObject["ifcType"] is string candidateIfcType)
      {
        // Dictionary.GetValueOrDefault isn't available on net48 (Tekla2025) - TryGetValue works on
        // both TFMs identically.
        candidateCounts.TryGetValue(candidateIfcType, out int existingCount);
        candidateCounts[candidateIfcType] = existingCount + 1;
      }

      // Every TryEnrichXxx call below is documented as "never throws, a miss just returns false" -
      // but found from a live Tekla receive that a single malformed numeric literal anywhere in the
      // file (a genuinely new case IfcGeometryReaders.TryReadNumber didn't guard against) could still
      // throw all the way up through here, aborting enrichment for the ENTIRE file - every object in
      // it, not just the one with the bad value, fell back to DirectShape. Wrapping the per-candidate
      // work is defense-in-depth beyond that specific fix: any future extractor bug (a genuinely new
      // export shape this hasn't been tested against yet) now only costs this one candidate, matching
      // the "soft-fail per object" design every extractor in this project is already built around.
      try
      {
        // Not part of the enrichedCount/wallEnrichedCount-style stats below - this marks an
        // intentional skip (no independent geometry to reconstruct), not a native reconstruction.
        if (TryEnrichElementAssembly(dataObject))
        {
          elementAssemblySkippedCount++;
          continue;
        }

        bool enrichedWall = TryEnrichWall(
          dataObject,
          graph,
          globalIdToExpressId,
          elementToMaterial,
          hostToOpenings,
          synthesizedOpenings,
          lengthScaleToMm
        );
        bool enrichedFloor =
          !enrichedWall
          && TryEnrichFloor(
            dataObject,
            graph,
            globalIdToExpressId,
            elementToMaterial,
            hostToOpenings,
            synthesizedOpenings,
            lengthScaleToMm
          );
        bool enrichedFoundationSlab =
          !enrichedWall
          && !enrichedFloor
          && TryEnrichFoundationSlab(
            dataObject,
            graph,
            globalIdToExpressId,
            hostToOpenings,
            synthesizedOpenings,
            lengthScaleToMm
          );
        bool enrichedGrid =
          !enrichedWall
          && !enrichedFloor
          && !enrichedFoundationSlab
          && TryEnrichGrid(dataObject, graph, globalIdToExpressId, synthesizedGrids, lengthScaleToMm);
        if (
          TryEnrichColumn(dataObject, graph, globalIdToExpressId, elementToMaterial, lengthScaleToMm)
          || TryEnrichBeam(dataObject, graph, globalIdToExpressId, elementToMaterial, lengthScaleToMm)
          || TryEnrichFooting(dataObject, graph, globalIdToExpressId, lengthScaleToMm)
          || enrichedWall
          || enrichedFloor
          || enrichedFoundationSlab
          || enrichedGrid
        )
        {
          enrichedCount++;
          if (enrichedWall)
          {
            wallEnrichedCount++;
          }
          if (enrichedFloor)
          {
            floorEnrichedCount++;
          }
          if (enrichedFoundationSlab)
          {
            foundationSlabEnrichedCount++;
          }
          if (enrichedGrid)
          {
            gridEnrichedCount++;
          }
        }
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        logger.LogWarning(
          ex,
          "RevitNativeSchemaEnricher: enrichment threw for applicationId={ApplicationId} - left as DirectShape rather than aborting the whole file.",
          dataObject.applicationId
        );
      }
    }

    // Openings have no pre-existing DataObject of their own to enrich (see TryBuildOpeningDataObject's
    // remarks) - inject the synthesized ones directly into the root Collection's own elements so the
    // connector's normal unpacking/traversal (which walks Collection.elements recursively) discovers
    // them exactly like any other top-level received object. Every real Speckle receive root is a
    // Collection in practice; if this one somehow isn't, soft-fail (log and drop them) rather than throw.
    // Grid axes beyond the first (see TryEnrichGrid's remarks) have no pre-existing DataObject of their
    // own either - same injection mechanism as synthesized openings.
    var synthesized = synthesizedOpenings.Concat(synthesizedGrids).ToList();
    if (synthesized.Count > 0)
    {
      if (rootObject is Collection rootCollection)
      {
        rootCollection.elements.AddRange(synthesized);
      }
      else
      {
        logger.LogWarning(
          "RevitNativeSchemaEnricher: root object is a {Type}, not a Collection - {Count} synthesized object(s) could not be injected into the tree.",
          rootObject.GetType().Name,
          synthesized.Count
        );
      }
    }

    logger.LogInformation(
      "RevitNativeSchemaEnricher: enriched {Count} object(s) for native reconstruction ({WallCount} wall(s), {FloorCount} floor(s), {FoundationSlabCount} foundation slab(s), {GridCount} grid(s), {OpeningCount} synthesized opening(s), {SynthesizedGridCount} synthesized grid line(s), {AssemblySkippedCount} element assembly container(s) skipped). ifcType candidates seen: {Candidates}",
      enrichedCount,
      wallEnrichedCount,
      floorEnrichedCount,
      foundationSlabEnrichedCount,
      gridEnrichedCount,
      synthesizedOpenings.Count,
      synthesizedGrids.Count,
      elementAssemblySkippedCount,
      string.Join(", ", candidateCounts.Select(p => $"{p.Key}={p.Value}"))
    );
  }

  // Every check below is a TryX - a miss just means this object isn't touched and keeps its existing
  // (DirectShape-bound) shape, never a thrown exception.
  private bool TryEnrichColumn(
    DataObject dataObject,
    StepGraph graph,
    IReadOnlyDictionary<string, uint> globalIdToExpressId,
    IReadOnlyDictionary<uint, uint> elementToMaterial,
    double lengthScaleToMm
  )
  {
    if (dataObject.applicationId is not { } globalId || !globalIdToExpressId.TryGetValue(globalId, out uint expressId))
    {
      return false;
    }

    if (
      dataObject["ifcType"] is not string ifcType
      || !string.Equals(ifcType, "IfcColumn", StringComparison.OrdinalIgnoreCase)
    )
    {
      return false;
    }

    if (!IfcPlacementResolver.TryResolveElementPlacement(graph, expressId, out var placement) || placement is null)
    {
      return false;
    }

    if (!TryReadObjectType(graph, expressId, out string? columnTypeName))
    {
      // No ifcTypeName means IfcTypeMappingProvider can never key a mapping to this column, and (per
      // the "no wrong guess" rule) it must not fall through to a first-available-symbol guess either -
      // so without an ObjectType string this column can't be enriched at all, same as IfcEnrichBeam.
      return false;
    }

    // Best-effort profile string - only circular/rectangular are recognized. A steel I/H-shape (or any
    // other catalog section) leaves this null, but that must NOT stop enrichment (fixed from a live
    // receive: steel columns were silently never enriched at all, so they could never even appear in
    // the IFC-type-mapping dialog to be fixed manually) - builtInCategory/location/ifcTypeName are set
    // regardless, exactly mirroring TryEnrichBeam's design. Unmapped non-standard profiles fall to
    // DirectShape via StructuralFramingHelper.ResolveSymbol's safety-net throw, same as an unmapped beam
    // type, until the user maps them via the dialog.
    //
    // BUG FIX: world origin is NOT just the element's own ObjectPlacement - found from a live receive
    // where every instance of a repeated round-column type landed at the exact same position. In that
    // real export, every instance's ObjectPlacement (and even the IfcRepresentationMap's
    // MappingOrigin) was identical/identity; the actual per-instance offset lived entirely in the
    // extrudedSolid's own local Position (see IfcProfileExtractor's remarks) - so whenever a
    // SweptSolid extrusion resolves at all, its own Position wins over the plain placement origin.
    //
    // FURTHER FALLBACK (found from a real ArchiCAD-exported file): a column can have NO SweptSolid
    // Body at all - only a raw Tessellation mesh, with no Axis/FootPrint either - meaning there is no
    // extrusion Position to read. In that case only, fall back to the element's own placement origin
    // directly - safe specifically because it only applies when there's no repeated-type extrusion
    // Position to prefer in the first place, so the bug above can't resurface.
    //
    // BUG FIX: the extrusion's own Depth/ExtrudedDirection were being read and then discarded, so
    // every column - regardless of its real height - got a single SOG.Point location. On receive,
    // IfcColumnBeamToTeklaBeamConverter treats any Point as "no captured axis" and inserts a
    // hardcoded 3000mm segment (DEFAULT_POINT_PLACED_LENGTH_MM), silently wrong for anything else.
    // The extrusion's own Position.Origin is the section's centroid (a symmetric profile is always
    // defined around local (0,0)), so Origin -> Origin + ExtrudedDirection*Depth is the column's true
    // base-to-top axis through that centroid - same technique TryResolveAxisWithFallbacks already
    // uses for beams. Falls back to the single-point form only when no clean extrusion is available
    // at all (the ArchiCAD case above), same as before this fix.
    string? profile = null;
    double? columnRotationDegrees = null;
    IfcVector3 localOrigin = IfcVector3.Zero;
    IfcVector3? localTop = null;
    if (
      IfcProfileExtractor.TryResolveExtrudedProfile(
        graph,
        expressId,
        out uint profileDefId,
        out double depthMm,
        out var extrusionLocalPosition,
        out var extrudedDirection
      )
    )
    {
      localOrigin = extrusionLocalPosition.Origin;
      localTop = localOrigin + (extrudedDirection * depthMm);
      bool isCircularColumn = IfcProfileExtractor.TryReadCircleProfile(graph, profileDefId, out double diameterMm);
      if (isCircularColumn)
      {
        // "D{diameter}" convention - see StructuralFramingHelper.TryParseCircularProfileMm. No
        // rotation to capture either - a circular section is symmetric regardless of plan rotation.
        profile = FormattableString.Invariant($"D{diameterMm * lengthScaleToMm:0.###}");
      }
      else if (
        IfcProfileExtractor.TryReadRectangleProfile(graph, profileDefId, out double widthMm, out double heightMm)
      )
      {
        // "{height}*{width}" convention - see StructuralFramingHelper.TryParseRectangularProfileMm.
        profile = FormattableString.Invariant($"{heightMm * lengthScaleToMm:0.###}*{widthMm * lengthScaleToMm:0.###}");
      }

      // Rotation: a non-square/non-circular column's TRUE plan orientation (which way its "long side"
      // faces) was never captured at all before this - unlike RevitColumnBeamToTeklaBeamConverter,
      // which reads an actual Revit "rotation" instance parameter, there's no equivalent parameter for
      // an IFC-sourced column, only geometry.
      //
      // BUG FIX: this used to be nested inside the TryReadRectangleProfile branch above, silently
      // skipping every STEEL (I/H-shape) column entirely - confirmed live (two rotated steel columns,
      // one a full 90 degrees, came through unrotated). Moved out so it runs for ANY non-circular
      // profile shape, resolved (rectangle) or not (I-shape, falls to ifcTypeName mapping for its
      // profile string but is still fully placed/oriented geometry). ReadProfilePosition reads by
      // fixed attribute index (Position is always IfcParameterizedProfileDef's 3rd attribute,
      // inherited by every parameterized profile subtype including IfcIShapeProfileDef) - not
      // rectangle-specific, so this works the same way regardless of shape.
      //
      // Computed as the signed angle, in plan (around world vertical), between world +X and the
      // profile's own local Y-axis - 0 for a column whose section already lines up with the project's
      // own X/Y axes (the common case), a real nonzero value for one rotated on plan.
      //
      // BUG FIX: originally measured against local X, which needed a uniform +90 degrees on every
      // column to be correct (confirmed live - every column, not just some, was off by exactly the
      // same amount, pointing at a wrong reference axis rather than a sign error). Switched to local Y
      // to match Tekla's own reference convention, consistent with the beam rotation fix (which
      // already correctly uses local Y as the canonical height/depth reference, per
      // IfcIShapeProfileDef's OverallDepth - confirmed working - and Tekla's own Position.Rotation=TOP
      // apparently expects the same axis for a vertical part's "zero rotation" baseline).
      if (!isCircularColumn)
      {
        var columnProfilePosition = IfcProfileExtractor.ReadProfilePosition(graph, profileDefId);
        var columnToWorld = placement.Compose(extrusionLocalPosition).Compose(columnProfilePosition);
        var worldProfileYAxis = columnToWorld.TransformDirection(new IfcVector3(0, 1, 0)).Normalized();
        double rotationDegrees = Math.Atan2(worldProfileYAxis.Y, worldProfileYAxis.X) * 180.0 / Math.PI;
        if (Math.Abs(rotationDegrees) > 1e-3)
        {
          columnRotationDegrees = rotationDegrees;
        }
      }
    }

    dataObject["builtInCategory"] = "OST_StructuralColumns";
    dataObject["location"] = localTop is { } top
      ? BuildScaledLineLocation(placement.TransformPoint(localOrigin), placement.TransformPoint(top), lengthScaleToMm)
      : new SOG.Point(
        placement.TransformPoint(localOrigin).X * lengthScaleToMm,
        placement.TransformPoint(localOrigin).Y * lengthScaleToMm,
        placement.TransformPoint(localOrigin).Z * lengthScaleToMm,
        "mm"
      );
    dataObject["ifcTypeName"] = columnTypeName;
    if (profile is not null)
    {
      dataObject["profile"] = profile;
    }
    if (columnRotationDegrees is { } rotation)
    {
      dataObject["rotationDegrees"] = rotation;
    }
    // Best-effort real material (e.g. "Ortbeton - bewehrt Verputzt") - never required: a name that
    // isn't a real Tekla catalog entry (a generic German material description isn't a Tekla-catalog
    // concrete grade) falls back to a sensible default on receive, same "best-effort" spirit as
    // profile above. Without this, every column silently defaulted to a hardcoded STEEL material even
    // when it's plainly concrete (confirmed live - captured here purely for IfcColumnBeamToTeklaBeamConverter
    // to prefer a concrete-shaped default, e.g. "Concrete_Undefined", over the steel one whenever this
    // column also resolved a rectangular/circular - i.e. concrete-typical - profile above).
    if (IfcMaterialThicknessExtractor.TryGetMaterialName(graph, elementToMaterial, expressId, out string? materialName))
    {
      dataObject["material"] = materialName;
    }

    return true;
  }

  // Isolated pad footings only (PredefinedType .PAD_FOOTING.) - strip/pile-cap/footing-beam footings
  // need a different location shape (a curve/multi-point boundary, not a point) and are left untouched
  // rather than guessed at. Mirrors TryEnrichColumn's extraction exactly (same SweptSolid+
  // RectangleProfileDef+extrusion-Position shape), but with a materially different output shape:
  // FoundationToHostConverter.ApplyFootingDimensions expects a vertical SOG.Line (not a point) whose
  // LENGTH encodes the footing's thickness, plus a "{width}*{depth}" profile string for its plan
  // footprint - see that method's own remarks, which this mirrors.
  //
  // Deliberately does NOT set ifcTypeName (unlike every other enriched category): StructuralFramingHelper
  // .ResolveSymbol skips its rectangular/circular auto-dimension tiers entirely for
  // OST_StructuralFoundation (a footing's profile string means something different there - see
  // ResolveSymbol's own remarks), so if ifcTypeName were set, the safety-net throw added for
  // beams/columns would fire unconditionally for every footing (nothing else in that category can ever
  // satisfy it) - preventing FoundationToHostConverter from ever getting an initial symbol to resize in
  // the first place. Leaving ifcTypeName unset lets ResolveSymbol's existing FirstOrDefault fallback
  // supply an initial symbol exactly as it already does for Tekla footings, which
  // ApplyFootingDimensions then corrects via its own swap-or-resize logic - unchanged, proven
  // machinery, not something this feature needs to touch.
  //
  // The IfcFooting/.PAD_FOOTING. entry point itself remains unverified against real data (this
  // feature's real test file has zero IfcFooting entities), but the extraction it delegates to
  // (TryEnrichRectangularPadFootingFromBody) is exercised for real via TryEnrichFoundationSlab's
  // IfcSlab/.BASESLAB. "Pile Cap" fallback below - the same real file's isolated footings, just modeled
  // as IfcSlab rather than IfcFooting.
  private static bool TryEnrichFooting(
    DataObject dataObject,
    StepGraph graph,
    IReadOnlyDictionary<string, uint> globalIdToExpressId,
    double lengthScaleToMm
  )
  {
    if (dataObject.applicationId is not { } globalId || !globalIdToExpressId.TryGetValue(globalId, out uint expressId))
    {
      return false;
    }

    if (
      dataObject["ifcType"] is not string ifcType
      || !string.Equals(ifcType, "IfcFooting", StringComparison.OrdinalIgnoreCase)
    )
    {
      return false;
    }

    // BUG FIX (found from a live receive against a Tekla-authored .ifc file): Tekla's own IFC exporter
    // leaves every IfcFooting's PredefinedType as .NOTDEFINED. rather than setting .PAD_FOOTING.
    // explicitly, even for a plainly isolated, square pad footing with a clean rectangular SweptSolid
    // Body - confirmed directly in the file. Originally required .PAD_FOOTING. strictly to avoid
    // mis-treating a strip/pile-cap/footing-beam footing as an isolated pad; since
    // TryEnrichRectangularPadFootingFromBody already gates on the SHAPE being a rectangular extrusion
    // (which a strip footing's long, non-square profile can still technically satisfy), .NOTDEFINED. is
    // now also accepted - "unspecified" is not the same as "known to be something else", and rejecting
    // every .NOTDEFINED. footing outright was too strict for at least this real exporter's default
    // behavior. Other INCOMPATIBLE predefined types (e.g. .CAISSON_FOUNDATION.) are still excluded.
    if (
      !graph.Lookup.TryGetValue(expressId, out var node)
      || node.Entity.Count <= 8
      || node.Entity[8] is not StepSymbol predefinedType
    )
    {
      return false;
    }

    // .STRIP_FOOTING. (e.g. a wall footing) is a genuinely different shape from an isolated pad -
    // confirmed against a real Revit export: same overall entity shape (a single IfcExtrudedAreaSolid
    // over an IfcRectangleProfileDef, extruded vertically by the footing's thickness), but the
    // rectangle profile itself IS the footing's full plan footprint (long and thin, running the
    // wall's length) rather than a small square block - treating it like a pad footing would produce
    // a single tiny thickness-only segment sitting at one point, silently dropping the run entirely.
    // Routes through the SAME flat-mat/ContourPlate machinery as a foundation slab instead (see
    // TryEnrichStripFootingFromBody) - correct either way, since a rectangular strip footing's plan
    // footprint is just a (very elongated) 4-corner rectangle, same shape a "Bodenplatte" mat has.
    if (predefinedType.Name.ToString().Equals("STRIP_FOOTING", StringComparison.OrdinalIgnoreCase))
    {
      return TryEnrichStripFootingFromBody(dataObject, graph, expressId, lengthScaleToMm);
    }

    if (
      !(
        predefinedType.Name.ToString().Equals("PAD_FOOTING", StringComparison.OrdinalIgnoreCase)
        || predefinedType.Name.ToString().Equals("NOTDEFINED", StringComparison.OrdinalIgnoreCase)
      )
    )
    {
      return false;
    }

    return TryEnrichRectangularPadFootingFromBody(dataObject, graph, expressId, lengthScaleToMm);
  }

  // A strip footing's IfcExtrudedAreaSolid sweeps a rectangle that IS the footing's full plan
  // footprint (unlike a column/pad footing's symmetric-around-center profile) vertically upward by
  // its thickness - so this reads the same shape TryEnrichRectangularPadFootingFromBody does, but
  // builds a 4-corner Polyline boundary (mirrors IfcOpeningExtractor's rectangle-corner technique,
  // including composing the profile's own Position - confirmed non-identity in real data) instead of
  // a single vertical thickness line, and dispatches through the flat-mat/ContourPlate path
  // (OST_StructuralFoundation + SOG.Polyline location -> IfcFoundationToTeklaConverter ->
  // IfcFloorToContourPlateConverter) rather than the beam-shaped pad-footing path.
  private static bool TryEnrichStripFootingFromBody(
    DataObject dataObject,
    StepGraph graph,
    uint expressId,
    double lengthScaleToMm
  )
  {
    if (
      !IfcProfileExtractor.TryResolveExtrudedProfile(
        graph,
        expressId,
        out uint profileDefId,
        out double depthMm,
        out var extrusionLocalPosition
      )
    )
    {
      return false;
    }

    if (!IfcProfileExtractor.TryReadRectangleProfile(graph, profileDefId, out double xDimMm, out double yDimMm))
    {
      // e.g. an L-shaped or other non-rectangular strip footing cross-section - not yet supported.
      return false;
    }

    if (
      !IfcPlacementResolver.TryResolveElementPlacement(graph, expressId, out var elementPlacement)
      || elementPlacement is null
    )
    {
      return false;
    }

    var profilePosition = IfcProfileExtractor.ReadProfilePosition(graph, profileDefId);
    var combined = elementPlacement.Compose(extrusionLocalPosition).Compose(profilePosition);

    double halfX = xDimMm / 2;
    double halfY = yDimMm / 2;
    var localCorners = new[]
    {
      new IfcVector3(-halfX, -halfY, 0),
      new IfcVector3(halfX, -halfY, 0),
      new IfcVector3(halfX, halfY, 0),
      new IfcVector3(-halfX, halfY, 0),
    };

    // The boundary must be the footing's TOP face - IfcFloorToContourPlateConverter always applies
    // Position.Depth=BEHIND (thickness extends downward from the given boundary), same convention
    // every other flat-mat/slab boundary this feature produces already follows. The extrusion's own
    // Position is its BASE (ExtrudedDirection*Depth reaches the top; assumes local +Z like
    // TryEnrichRectangularPadFootingFromBody's identical assumption, unverified in general but true
    // for every extrusion sampled in this feature's real files so far), so shift each corner up by
    // the full depth along the extrusion's own world-transformed direction.
    var worldUpShift = elementPlacement
      .Compose(extrusionLocalPosition)
      .TransformDirection(new IfcVector3(0, 0, depthMm));

    var worldValues = new List<double>(localCorners.Length * 3);
    foreach (var corner in localCorners)
    {
      var world = combined.TransformPoint(corner) + worldUpShift;
      worldValues.Add(world.X * lengthScaleToMm);
      worldValues.Add(world.Y * lengthScaleToMm);
      worldValues.Add(world.Z * lengthScaleToMm);
    }

    dataObject["builtInCategory"] = "OST_StructuralFoundation";
    dataObject["location"] = new SOG.Polyline
    {
      value = worldValues,
      closed = true,
      units = "mm",
    };
    // "PL{thickness}" - see IfcFloorToContourPlateConverter's profile convention.
    dataObject["profile"] = FormattableString.Invariant($"PL{depthMm * lengthScaleToMm:0.###}");

    return true;
  }

  // Shared by TryEnrichFooting (IfcFooting/.PAD_FOOTING.) and TryEnrichFoundationSlab's Body-based
  // fallback (IfcSlab/.BASESLAB. entities with no FootPrint representation) - both need the exact same
  // extraction shape: a rectangular SweptSolid Body + extrusion Position, output as a vertical SOG.Line
  // (length = thickness) + "{width}*{depth}" profile string, matching
  // FoundationToHostConverter.ApplyFootingDimensions's documented contract. Confirmed from a live receive
  // that BOTH entity kinds can carry this exact shape - a model exported "M_Pile Cap-2/4 Pile" isolated
  // footings as IfcSlab/.BASESLAB. (SweptSolid Body, no FootPrint) rather than as IfcFooting.
  private static bool TryEnrichRectangularPadFootingFromBody(
    DataObject dataObject,
    StepGraph graph,
    uint expressId,
    double lengthScaleToMm
  )
  {
    if (
      !IfcProfileExtractor.TryResolveExtrudedProfile(
        graph,
        expressId,
        out uint profileDefId,
        out double depthMm,
        out var extrusionLocalPosition
      )
    )
    {
      return false;
    }

    if (
      !IfcProfileExtractor.TryReadRectangleProfile(graph, profileDefId, out double widthMm, out double footingDepthMm)
    )
    {
      // e.g. a circular pile/pad footing - not yet supported, never guessed.
      return false;
    }

    if (!IfcPlacementResolver.TryResolveElementPlacement(graph, expressId, out var placement) || placement is null)
    {
      return false;
    }

    var worldBase = placement.TransformPoint(extrusionLocalPosition.Origin);
    // ExtrudedDirection wasn't independently re-verified here (unlike TryResolveBodyExtrusionDepth's
    // own +Z check) - every extrusion direction sampled elsewhere in this feature's real file was +Z,
    // so this assumes the same rather than re-deriving a direction vector for a category with no real
    // sample to confirm it against.
    var worldTop = worldBase + placement.TransformDirection(new IfcVector3(0, 0, depthMm));

    dataObject["builtInCategory"] = "OST_StructuralFoundation";
    dataObject["location"] = new SOG.Line
    {
      start = new SOG.Point(
        worldBase.X * lengthScaleToMm,
        worldBase.Y * lengthScaleToMm,
        worldBase.Z * lengthScaleToMm,
        "mm"
      ),
      end = new SOG.Point(
        worldTop.X * lengthScaleToMm,
        worldTop.Y * lengthScaleToMm,
        worldTop.Z * lengthScaleToMm,
        "mm"
      ),
      units = "mm",
    };
    // "{width}*{depth}" - see FoundationToHostConverter.ApplyFootingDimensions.
    dataObject["profile"] = FormattableString.Invariant(
      $"{widthMm * lengthScaleToMm:0.###}*{footingDepthMm * lengthScaleToMm:0.###}"
    );

    return true;
  }

  // Last-resort fallback for BASESLAB entries with no FootPrint AND no rectangular Body profile -
  // confirmed from a live receive to be cylindrical "Pile" foundations, in TWO different geometry
  // encodings within the SAME model (different pile diameter types, exported differently by the same
  // tool): some have an AdvancedBrep Body with no profile at all (same class of gap as a beam's
  // mitered-end AdvancedBrep body), others have a perfectly clean SweptSolid Body with a CIRCULAR
  // profile - rejected by TryEnrichRectangularPadFootingFromBody's rectangle-only check, but still a
  // real extrusion with a real Position, unlike the AdvancedBrep case. Mirrors TryEnrichBeam's
  // minimal-requirements design - only a position (as a POINT, not a line/profile -
  // FoundationToHostConverter's isolated-footing dispatch accepts either) and ifcTypeName are required,
  // making the IFC-type-mapping dialog the ONLY resolution path (there is no usable profile shape to
  // resize from either way). StructuralFramingHelper.ResolveSymbol's safety-net throw (gated on
  // ifcTypeName, applies to every category including OST_StructuralFoundation already - no changes
  // needed there) falls this back to DirectShape until the user maps it via the dialog.
  //
  // BUG FIX (found from a live receive - 216 piles all landed on the exact same point, same class of
  // bug as the column extrusion-Position issue fixed earlier in this feature): the element's own
  // ObjectPlacement is NOT a usable position here - confirmed every pile in the real file shares the
  // IDENTICAL local placement point relative to the same parent (an exporter convention, not a bug in
  // the file). This REQUIRES a genuine per-instance anchor to resolve (see
  // TryResolvePileLocalAnchor) - it deliberately does NOT fall back to the placement origin alone,
  // since that would silently reproduce the exact overlap bug this fix exists for. A pile with neither
  // a usable extrusion Position nor a readable Body vertex is left as DirectShape rather than risk
  // stacking it on every other unenrichable pile.
  private static bool TryEnrichPileFromPlacement(
    DataObject dataObject,
    StepGraph graph,
    uint expressId,
    double lengthScaleToMm
  )
  {
    if (!IfcPlacementResolver.TryResolveElementPlacement(graph, expressId, out var placement) || placement is null)
    {
      return false;
    }

    if (!TryReadObjectType(graph, expressId, out string? pileTypeName))
    {
      return false;
    }

    if (!TryResolvePileLocalAnchor(graph, expressId, out var localAnchor))
    {
      return false;
    }

    var worldOrigin = placement.TransformPoint(localAnchor);

    dataObject["builtInCategory"] = "OST_StructuralFoundation";
    dataObject["location"] = new SOG.Point(
      worldOrigin.X * lengthScaleToMm,
      worldOrigin.Y * lengthScaleToMm,
      worldOrigin.Z * lengthScaleToMm,
      "mm"
    );
    dataObject["ifcTypeName"] = pileTypeName;

    return true;
  }

  // Tries every per-instance position source this feature's live-file investigation found across the
  // two different pile geometry encodings in the same real model: a SweptSolid extrusion's own Position
  // first (works regardless of the profile's SHAPE - rectangular, circular, or anything else
  // IfcProfileExtractor doesn't specifically recognize, since only the Position matters here, not the
  // cross-section), falling back to a raw AdvancedBrep vertex (see IfcAdvancedBrepExtractor) for piles
  // with no SweptSolid Body at all.
  private static bool TryResolvePileLocalAnchor(StepGraph graph, uint expressId, out IfcVector3 localAnchor)
  {
    if (IfcProfileExtractor.TryResolveExtrudedProfile(graph, expressId, out _, out _, out var extrusionPosition))
    {
      localAnchor = extrusionPosition.Origin;
      return true;
    }

    return IfcAdvancedBrepExtractor.TryReadLowestBodyVertex(graph, expressId, out localAnchor);
  }

  // Shared by TryEnrichBeam and TryEnrichWall: tries every axis source found across this feature's
  // live-file investigation, in order of reliability. (1) The declared 'Axis' representation - always
  // wins unconditionally when present, so a Revit-authored element (which always has one) never
  // reaches the other two tiers at all. (2) The extrusion's own Position -> Position + ExtrudedDirection
  // * Depth - not a heuristic, a real geometric fact about the extrusion, just generalizing the
  // vertical-line technique already used for footings/piles to any direction; used for a real ArchiCAD
  // beam confirmed to have a clean rectangular SweptSolid Body but no 'Axis' at all. (3) The Body
  // bounding-box heuristic (see IfcBodyBoundingBoxAxisHeuristic's remarks) - genuinely approximate,
  // last resort for elements with neither a declared Axis nor a clean extrusion (e.g. a Tekla panel
  // whose cut geometry made Auto/SweptSolid export fail entirely, dropping to a raw Brep body).
  private static bool TryResolveAxisWithFallbacks(
    StepGraph graph,
    uint expressId,
    out IfcVector3 localStart,
    out IfcVector3 localEnd,
    out string axisSource
  )
  {
    if (IfcAxisExtractor.TryExtractAxisLine(graph, expressId, out localStart, out localEnd))
    {
      axisSource = "Axis";
      return true;
    }

    if (
      IfcProfileExtractor.TryResolveExtrudedProfile(
        graph,
        expressId,
        out _,
        out double depthMm,
        out var extrusionPosition,
        out var extrudedDirection
      )
    )
    {
      localStart = extrusionPosition.Origin;
      localEnd = extrusionPosition.Origin + extrudedDirection * depthMm;
      axisSource = "ExtrusionPositionAndDepth";
      return true;
    }

    if (
      IfcBodyBoundingBoxAxisHeuristic.TryComputeAxisFromBodyBoundingBox(graph, expressId, out localStart, out localEnd)
    )
    {
      axisSource = "BoundingBoxHeuristic";
      return true;
    }

    axisSource = "";
    return false;
  }

  private static SOG.Line BuildScaledLineLocation(IfcVector3 worldStart, IfcVector3 worldEnd, double lengthScaleToMm) =>
    new()
    {
      start = new SOG.Point(
        worldStart.X * lengthScaleToMm,
        worldStart.Y * lengthScaleToMm,
        worldStart.Z * lengthScaleToMm,
        "mm"
      ),
      end = new SOG.Point(
        worldEnd.X * lengthScaleToMm,
        worldEnd.Y * lengthScaleToMm,
        worldEnd.Z * lengthScaleToMm,
        "mm"
      ),
      units = "mm",
    };

  // Builds a true curved location from 3 world-space points (start, a point ON the arc, end) - the
  // same 3-point form Revit's own DB.Arc.Create(XYZ,XYZ,XYZ) expects directly, so no
  // circumcenter/sweep-direction computation is needed here (unlike the footprint/profile arc
  // tessellation elsewhere in this project, which has no such direct host-API equivalent). `plane` is
  // only ever consulted by ArcConverterToHost's degenerate full-circle branch (start==end, never true
  // for a real wall axis) - still built from the 3 points so nothing downstream sees a null/degenerate
  // plane, mirroring the equivalent Tekla ArcLocation construction exactly.
  private static SOG.Arc BuildScaledArcLocation(
    IfcVector3 worldStart,
    IfcVector3 worldMid,
    IfcVector3 worldEnd,
    double lengthScaleToMm
  )
  {
    var scaledStart = new SOG.Point(
      worldStart.X * lengthScaleToMm,
      worldStart.Y * lengthScaleToMm,
      worldStart.Z * lengthScaleToMm,
      "mm"
    );
    var scaledMid = new SOG.Point(
      worldMid.X * lengthScaleToMm,
      worldMid.Y * lengthScaleToMm,
      worldMid.Z * lengthScaleToMm,
      "mm"
    );
    var scaledEnd = new SOG.Point(
      worldEnd.X * lengthScaleToMm,
      worldEnd.Y * lengthScaleToMm,
      worldEnd.Z * lengthScaleToMm,
      "mm"
    );

    var v1 = (worldMid - worldStart) * lengthScaleToMm;
    var v2 = (worldEnd - worldStart) * lengthScaleToMm;
    var normal = v1.Cross(v2).Normalized();
    var xdir = v2.Normalized();
    var ydir = normal.Cross(xdir).Normalized();

    return new SOG.Arc
    {
      startPoint = scaledStart,
      midPoint = scaledMid,
      endPoint = scaledEnd,
      plane = new SOG.Plane
      {
        origin = scaledMid,
        normal = new SOG.Vector(normal.X, normal.Y, normal.Z, "mm"),
        xdir = new SOG.Vector(xdir.X, xdir.Y, xdir.Z, "mm"),
        ydir = new SOG.Vector(ydir.X, ydir.Y, ydir.Z, "mm"),
        units = "mm",
      },
      units = "mm",
    };
  }

  // Beams frequently have no structured profile at all (an AdvancedBrep body with mitered end cuts -
  // confirmed via this feature's live-file investigation, not hypothetical), so unlike columns, this
  // does NOT gate enrichment on profile extraction succeeding. Axis (location) + ifcTypeName are the
  // only hard requirements - ifcTypeName is what lets IfcTypeMappingProvider resolve a symbol despite
  // the missing profile; StructuralFramingHelper.ResolveSymbol's safety-net throw handles the case
  // where neither profile nor an explicit mapping resolves, falling back to DirectShape rather than
  // guessing.
  //
  // Curved beams: tries IfcAxisExtractor.TryExtractAxisArc first (same reader added for curved walls -
  // both the ArchiCAD IfcIndexedPolyCurve+IfcArcIndex form and the Revit bare-IfcTrimmedCurve-over-
  // IfcCircle form), producing an SOG.Arc location instead of a line. The receive side
  // (IfcColumnBeamToTeklaBeamConverter) already had an SOG.Arc case ready and waiting - it's shared
  // with columns' curved-axis handling (RevitColumnBeamToTeklaBeamConverter.CreateArcPolyBeam) - so
  // this enrichment-side gap was the only thing missing for curved beams to round-trip.
  private bool TryEnrichBeam(
    DataObject dataObject,
    StepGraph graph,
    IReadOnlyDictionary<string, uint> globalIdToExpressId,
    IReadOnlyDictionary<uint, uint> elementToMaterial,
    double lengthScaleToMm
  )
  {
    if (dataObject.applicationId is not { } globalId || !globalIdToExpressId.TryGetValue(globalId, out uint expressId))
    {
      return false;
    }

    // IfcMember covers secondary/non-primary steel (braces, struts, purlins) - real exporters
    // (confirmed from a live receive - steel members were silently never enriched at all) commonly
    // classify these as IfcMember rather than IfcBeam, even though both route to the same
    // OST_StructuralFraming category and the same BeamToHostConverter.
    if (
      dataObject["ifcType"] is not string ifcType
      || !(
        string.Equals(ifcType, "IfcBeam", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ifcType, "IfcMember", StringComparison.OrdinalIgnoreCase)
      )
    )
    {
      return false;
    }

    bool axisIsArc = IfcAxisExtractor.TryExtractAxisArc(
      graph,
      expressId,
      out var localArcStart,
      out var localArcMid,
      out var localArcEnd
    );

    IfcVector3 localStart = IfcVector3.Zero;
    IfcVector3 localEnd = IfcVector3.Zero;
    if (!axisIsArc && !TryResolveAxisWithFallbacks(graph, expressId, out localStart, out localEnd, out _))
    {
      return false;
    }

    if (!IfcPlacementResolver.TryResolveElementPlacement(graph, expressId, out var placement) || placement is null)
    {
      return false;
    }

    if (!TryReadObjectType(graph, expressId, out string? beamTypeName))
    {
      // No ifcTypeName means IfcTypeMappingProvider can never key a mapping to this beam, and (per the
      // "no wrong guess" rule) it must not fall through to a first-available-symbol guess either - so
      // without an ObjectType string this beam can't be enriched at all, same as a missing profile.
      return false;
    }

    dataObject["builtInCategory"] = "OST_StructuralFraming";
    dataObject["location"] = axisIsArc
      ? BuildScaledArcLocation(
        placement.TransformPoint(localArcStart),
        placement.TransformPoint(localArcMid),
        placement.TransformPoint(localArcEnd),
        lengthScaleToMm
      )
      : BuildScaledLineLocation(
        placement.TransformPoint(localStart),
        placement.TransformPoint(localEnd),
        lengthScaleToMm
      );
    dataObject["ifcTypeName"] = beamTypeName;
    // Best-effort real material (e.g. "Ortbeton - bewehrt Verputzt") - see TryEnrichColumn's own
    // remarks; same capture, same reason (every beam silently defaulted to a hardcoded STEEL material
    // on receive even when plainly concrete, confirmed live).
    if (
      IfcMaterialThicknessExtractor.TryGetMaterialName(
        graph,
        elementToMaterial,
        expressId,
        out string? beamMaterialName
      )
    )
    {
      dataObject["material"] = beamMaterialName;
    }

    // Best-effort profile - works for some beams (e.g. clean SweptSolid bodies), but is never required:
    // even when it fails, the IFC-type-mapping tier in StructuralFramingHelper.ResolveSymbol (keyed on
    // ifcTypeName above) is the primary resolution path for beams.
    if (
      IfcProfileExtractor.TryResolveExtrudedProfile(
        graph,
        expressId,
        out uint profileDefId,
        out double beamExtrusionDepthMm,
        out var beamExtrusionPosition,
        out var beamExtrudedDirection
      )
    )
    {
      if (IfcProfileExtractor.TryReadCircleProfile(graph, profileDefId, out double diameterMm))
      {
        // No rotation to capture either - a circular section is symmetric regardless of rotation.
        dataObject["profile"] = FormattableString.Invariant($"D{diameterMm * lengthScaleToMm:0.###}");
      }
      else
      {
        // BUG FIX: a rectangle-profile beam's geometry showed up in a real Revit export in TWO
        // materially different shapes, and naively treating XDim as "width"/YDim as "height" is wrong
        // for either, just in different ways:
        //  (A) Cross-section swept ALONG the span - the extrusion direction is roughly HORIZONTAL
        //      (matches the beam's own axis), and XDim/YDim genuinely are the cross-section's two
        //      dimensions, but the profile's own 2D Position can still be rotated relative to the
        //      extrusion frame (confirmed: RefDirection=(0,1) instead of the default (1,0)), swapping
        //      which one ends up vertical - invisible for a square section, wrong for an asymmetric
        //      one. Resolved by checking which profile-local axis (X or Y) ends up more aligned with
        //      world-vertical (Z) after composing the full profile -> extrusion -> element chain.
        //  (B) Footprint (width x the beam's own LENGTH) extruded VERTICALLY by the true height -
        //      confirmed for a different real beam in the same file: XDim=5300mm (matching the beam's
        //      own length, not a cross-section dimension at all) with the extrusion direction roughly
        //      VERTICAL and Depth=800mm (the true height). Using XDim/YDim directly here (as case A
        //      does) produces nonsense like "400*5300" - a beam "width" of 5.3 metres. Detected by the
        //      extrusion direction being mostly vertical rather than mostly horizontal; height comes
        //      from the extrusion's own Depth, width from the SMALLER of XDim/YDim (the larger one
        //      being the beam's own length, not a cross-section dimension, and must be discarded).
        bool isRectangle = IfcProfileExtractor.TryReadRectangleProfile(
          graph,
          profileDefId,
          out double xDimMm,
          out double yDimMm
        );
        var worldExtrusionDirection = placement
          .Compose(beamExtrusionPosition)
          .TransformDirection(beamExtrudedDirection);
        bool verticalExtrusion = Math.Abs(worldExtrusionDirection.Z) > 0.7;

        if (isRectangle && verticalExtrusion)
        {
          // Pattern B - the discarded (larger, length) dimension carries no usable rotation info
          // either, so none is captured here.
          double heightMm = beamExtrusionDepthMm;
          double widthMm = Math.Min(xDimMm, yDimMm);
          dataObject["profile"] = FormattableString.Invariant(
            $"{heightMm * lengthScaleToMm:0.###}*{widthMm * lengthScaleToMm:0.###}"
          );
        }
        else if (!verticalExtrusion)
        {
          // Pattern A - works for a resolved rectangle (dimension string + rotation) AND for any
          // OTHER, unresolved profile shape (rotation only - no "{h}*{w}" string to build without a
          // dedicated dimension reader, ifcTypeName mapping remains the resolution path for those,
          // same as before this fix).
          //
          // BUG FIX: rotation used to be computed ONLY inside the resolved-rectangle branch, silently
          // skipping every STEEL (I/H-shape) beam entirely - confirmed live (a rotated steel beam came
          // through unrotated).
          var beamProfilePosition = IfcProfileExtractor.ReadProfilePosition(graph, profileDefId);
          var toWorld = placement.Compose(beamExtrusionPosition).Compose(beamProfilePosition);
          var worldXAxis = toWorld.TransformDirection(new IfcVector3(1, 0, 0)).Normalized();
          var worldYAxis = toWorld.TransformDirection(new IfcVector3(0, 1, 0)).Normalized();

          IfcVector3 heightAxisWorld;
          if (isRectangle)
          {
            bool xDimIsHeight = Math.Abs(worldXAxis.Z) > Math.Abs(worldYAxis.Z);
            double heightMm = xDimIsHeight ? xDimMm : yDimMm;
            double widthMm = xDimIsHeight ? yDimMm : xDimMm;
            dataObject["profile"] = FormattableString.Invariant(
              $"{heightMm * lengthScaleToMm:0.###}*{widthMm * lengthScaleToMm:0.###}"
            );
            heightAxisWorld = xDimIsHeight ? worldXAxis : worldYAxis;
          }
          else
          {
            // Unresolved shape (I/H-beam etc.) - IFC's own schema convention (IfcIShapeProfileDef's
            // OverallDepth, and every other IfcParameterizedProfileDef subtype this project has no
            // dedicated dimension reader for) always runs along the profile's own local Y. Unlike the
            // rectangle case, there's no ambiguity to resolve by picking "whichever is closer to
            // vertical" - Y IS the height/depth reference, so the FULL angle (not a bounded residual)
            // is the real rotation, which can be as much as a full 90-degree turn - confirmed needed
            // live (a steel column rotated a full 90 degrees has no "closer of two" to fall back on).
            heightAxisWorld = worldYAxis;
          }

          // Straight axis only (axisIsArc) - a curved beam's cross-section orientation isn't a single
          // angle the same way. Computed as the signed angle, around the beam's own world-space span
          // direction, between "true vertical projected into the cross-sectional plane" (Tekla's own
          // Rotation=TOP reference) and the chosen height axis.
          if (!axisIsArc)
          {
            var worldSpanDirection = (
              placement.TransformPoint(localEnd) - placement.TransformPoint(localStart)
            ).Normalized();
            var referenceUp = (
              IfcVector3.UnitZ - (worldSpanDirection * IfcVector3.UnitZ.Dot(worldSpanDirection))
            ).Normalized();
            double cosAngle = referenceUp.Dot(heightAxisWorld);
            double sinAngle = referenceUp.Cross(heightAxisWorld).Dot(worldSpanDirection);
            double rotationDegrees = Math.Atan2(sinAngle, cosAngle) * 180.0 / Math.PI;
            if (Math.Abs(rotationDegrees) > 1e-3)
            {
              dataObject["rotationDegrees"] = rotationDegrees;
            }
          }
        }
        // else: vertical extrusion + unresolved shape - no known real case yet (a vertically-extruded
        // I-beam footprint would be unusual), left alone rather than guessed at.
      }
    }

    return true;
  }

  // Matches WallToHostConverter.DEFAULT_HEIGHT_FEET (10ft = 3048mm), the same fallback that converter
  // already uses when no height data is captured at all. Used only when IfcProfileExtractor.
  // TryResolveBodyExtrusionDepth can't extract the wall's real height (e.g. a body shape that isn't a
  // simple boolean-of-extrusion chain) - a graceful degrade, not the normal case.
  private const double WALL_DEFAULT_HEIGHT_MM = 3048.0;

  // Unlike beams/columns, walls aren't Revit-repeated-family instances in this file (each has its own
  // real ObjectPlacement, confirmed by comparing two different walls' placement chains directly - the
  // "extrusion Position" bug fixed for columns doesn't apply here), so ObjectPlacement alone is a
  // reliable world position. No ifcTypeName-mapping-dialog tier exists for walls (unlike beams/
  // columns) - WallToHostConverter's existing width-match-or-first-available fallback is reused as-is,
  // matching how Tekla walls (which also carry no captured height/thickness data) already behave; not
  // a new gap introduced by this feature.
  //
  // Openings: gated exactly like TryEnrichFloor/TryEnrichFoundationSlab (every associated opening
  // must extract or the whole wall stays untouched/DirectShape) - see the BUG FIX comment below for
  // why this wasn't already the case.
  private bool TryEnrichWall(
    DataObject dataObject,
    StepGraph graph,
    IReadOnlyDictionary<string, uint> globalIdToExpressId,
    IReadOnlyDictionary<uint, uint> elementToMaterial,
    IReadOnlyDictionary<uint, List<uint>> hostToOpenings,
    List<DataObject> synthesizedOpenings,
    double lengthScaleToMm
  )
  {
    if (
      dataObject["ifcType"] is not string ifcType
      || !(
        string.Equals(ifcType, "IfcWall", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ifcType, "IfcWallStandardCase", StringComparison.OrdinalIgnoreCase)
      )
    )
    {
      return false;
    }

    if (dataObject.applicationId is not { } globalId || !globalIdToExpressId.TryGetValue(globalId, out uint expressId))
    {
      logger.LogWarning(
        "TryEnrichWall: applicationId={ApplicationId} did not match any GlobalId scanned from the file.",
        dataObject.applicationId
      );
      return false;
    }

    // A curved wall's 'Axis' is a single IfcArcIndex segment (start/mid/end on the arc), a genuinely
    // different shape from the straight-line case TryResolveAxisWithFallbacks handles - confirmed
    // common (~23% of walls) in a real ArchiCAD 26 export, not a rare edge case. Checked first since
    // it's mutually exclusive with the straight-line forms (a wall's axis curve is always exactly one
    // segment in every real file sampled so far, either a line or an arc, never both).
    bool axisIsArc = IfcAxisExtractor.TryExtractAxisArc(
      graph,
      expressId,
      out var localArcStart,
      out var localArcMid,
      out var localArcEnd
    );

    IfcVector3 localStart = IfcVector3.Zero;
    IfcVector3 localEnd = IfcVector3.Zero;
    string axisSource = "Axis (arc)";

    if (!axisIsArc && !TryResolveAxisWithFallbacks(graph, expressId, out localStart, out localEnd, out axisSource))
    {
      logger.LogWarning(
        "TryEnrichWall: applicationId={ApplicationId} expressId=#{ExpressId} - Axis extraction failed, and neither the extrusion-position nor the Body bounding-box axis fallback succeeded either.",
        dataObject.applicationId,
        expressId
      );
      return false;
    }

    if (axisSource != "Axis" && axisSource != "Axis (arc)")
    {
      logger.LogInformation(
        "TryEnrichWall: applicationId={ApplicationId} expressId=#{ExpressId} - no 'Axis' representation, falling back to {AxisSource}.",
        dataObject.applicationId,
        expressId,
        axisSource
      );
    }

    if (!IfcPlacementResolver.TryResolveElementPlacement(graph, expressId, out var placement) || placement is null)
    {
      logger.LogWarning(
        "TryEnrichWall: applicationId={ApplicationId} expressId=#{ExpressId} - ObjectPlacement resolution failed.",
        dataObject.applicationId,
        expressId
      );
      return false;
    }

    // BUG FIX: this previously never consulted hostToOpenings at all, unlike TryEnrichFloor/
    // TryEnrichFoundationSlab - so a wall with a real opening (e.g. a service penetration) would
    // still enrich to a native, SOLID Tekla wall, silently losing the opening with no warning at
    // all (worse than the floor/slab case, which at least fails safe back to DirectShape). Mirror
    // their gate exactly: every associated opening must extract (currently rectangular-only, see
    // TryBuildOpeningDataObject) or this wall is left completely untouched.
    var pendingOpenings = new List<DataObject>();
    if (hostToOpenings.TryGetValue(expressId, out var openingExpressIds))
    {
      foreach (uint openingExpressId in openingExpressIds)
      {
        if (!TryBuildOpeningDataObject(graph, openingExpressId, globalId, lengthScaleToMm, out var openingDataObject))
        {
          logger.LogWarning(
            "TryEnrichWall: applicationId={ApplicationId} expressId=#{ExpressId} has an associated opening #{OpeningExpressId} that couldn't be extracted (e.g. non-rectangular) - leaving this wall as DirectShape rather than risk losing its openings.",
            dataObject.applicationId,
            expressId,
            openingExpressId
          );
          return false;
        }
        pendingOpenings.Add(openingDataObject!);

        // TEMP DIAGNOSTIC (chasing "wall openings appear outside the wall") - see the matching
        // worldLocation on TryEnrichWall's own success log below for direct comparison.
        logger.LogInformation(
          "TryEnrichWall: opening expressId=#{OpeningExpressId} applicationId={OpeningApplicationId} worldLocation={WorldLocation}",
          openingExpressId,
          openingDataObject!.applicationId,
          DescribeLocation(openingDataObject["location"])
        );
      }
    }

    dataObject["builtInCategory"] = "OST_Walls";
    dataObject["location"] = axisIsArc
      ? BuildScaledArcLocation(
        placement.TransformPoint(localArcStart),
        placement.TransformPoint(localArcMid),
        placement.TransformPoint(localArcEnd),
        lengthScaleToMm
      )
      : BuildScaledLineLocation(
        placement.TransformPoint(localStart),
        placement.TransformPoint(localEnd),
        lengthScaleToMm
      );

    if (TryReadObjectType(graph, expressId, out string? wallTypeName))
    {
      // Lets an exact-name WallType match win if this file happens to round-trip through the same
      // Revit project it came from (its ObjectType is the original Revit type name) - see
      // RevitElementTypeResolver.FindWallType.
      dataObject["type"] = wallTypeName;
    }

    if (
      IfcMaterialThicknessExtractor.TryGetLayerSetThicknessMm(
        graph,
        elementToMaterial,
        expressId,
        out double thicknessMm
      )
    )
    {
      // Real height (see IfcProfileExtractor.TryResolveBodyExtrusionDepth's remarks: unwraps the
      // "Clipping"/IfcBooleanClippingResult body confirmed in the real file to reach the wall's own
      // pre-clip IfcExtrudedAreaSolid) wins when extractable; otherwise falls back to the same
      // hardcoded default an unenriched wall already gets. WALL_DEFAULT_HEIGHT_MM is already an
      // absolute millimetre value (not a raw file-unit reading), so it's deliberately NOT scaled -
      // only realHeightMm (a genuine extracted file-unit value) needs the conversion.
      double heightMm = IfcProfileExtractor.TryResolveBodyExtrusionDepth(graph, expressId, out double realHeightMm)
        ? realHeightMm * lengthScaleToMm
        : WALL_DEFAULT_HEIGHT_MM;

      // BUG FIX: found from a live receive - every wall converted at ~300mm (1ft) tall instead of the
      // intended height, because the number ORDER here was backwards.
      // StructuralFramingHelper.TryParseRectangularProfileMm's out-parameter NAMES don't match its
      // declared parameter POSITIONS: the first '*'-split number is always assigned to whichever
      // call-site variable is bound to its "heightMm" parameter (3rd argument), the second to whichever
      // is bound to "widthMm" (2nd argument) - regardless of what those local variables are called at
      // the call site. WallToHostConverter.ResolveWallType/ResolveHeightFeet's own call sites expect
      // "{height}*{thickness}" (height first) to work correctly - the exact same convention beams/
      // columns already use above, just not obvious from this class's own local variable naming.
      dataObject["profile"] = FormattableString.Invariant($"{heightMm:0.###}*{thicknessMm * lengthScaleToMm:0.###}");
    }

    synthesizedOpenings.AddRange(pendingOpenings);

    // TEMP DIAGNOSTIC (chasing "wall openings appear outside the wall"): log the wall's actual
    // world-space axis so it can be directly compared against TryBuildOpeningDataObject's own logged
    // world-space opening corners for the same wall - if the opening's corners fall outside the axis
    // span +/- half the logged thickness, the bug is upstream (placement composition); if they fall
    // inside, the bug is downstream (IfcWallToTeklaBeamConverter/IfcOpeningToBooleanPartConverter).
    logger.LogInformation(
      "TryEnrichWall: enriched applicationId={ApplicationId} expressId=#{ExpressId} type={Type} profile={Profile} axisSource={AxisSource} openings={OpeningCount} worldLocation={WorldLocation}",
      dataObject.applicationId,
      expressId,
      dataObject["type"],
      dataObject["profile"],
      axisSource,
      pendingOpenings.Count,
      DescribeLocation(dataObject["location"])
    );

    return true;
  }

  // TEMP DIAGNOSTIC helper - formats a location Base (SOG.Line/SOG.Arc/SOG.Polyline) as its raw
  // world-space coordinates for direct comparison across log lines. Remove once root-caused.
  private static string DescribeLocation(object? location) =>
    location switch
    {
      SOG.Line line =>
        $"Line(({line.start.x:F1},{line.start.y:F1},{line.start.z:F1})->({line.end.x:F1},{line.end.y:F1},{line.end.z:F1}))",
      SOG.Arc arc =>
        $"Arc(({arc.startPoint.x:F1},{arc.startPoint.y:F1},{arc.startPoint.z:F1})->mid({arc.midPoint.x:F1},{arc.midPoint.y:F1},{arc.midPoint.z:F1})->({arc.endPoint.x:F1},{arc.endPoint.y:F1},{arc.endPoint.z:F1}))",
      SOG.Polyline polyline =>
        $"Polyline[{string.Join(
          ";",
          Enumerable
            .Range(0, polyline.value.Count / 3)
            .Select(i =>
              $"({polyline.value[i * 3]:F1},{polyline.value[i * 3 + 1]:F1},{polyline.value[i * 3 + 2]:F1})"
            )
        )}]",
      null => "null",
      _ => location.GetType().Name,
    };

  // IfcSlab covers floors, roofs, and base slabs (distinguished by PredefinedType, attribute index 8) -
  // only .FLOOR. is in scope here; roofs/base-slabs are left untouched for a future phase rather than
  // guessed at.
  //
  // SAFETY GATE (found from a live receive, not hypothetical): a floor's Body can be a "CSG" boolean
  // (base slab minus its shaft/opening voids) even when its FootPrint boundary is a simple straight
  // polygon - confirmed for a real floor with 4 shaft openings and a perfectly straight 12-point
  // boundary. Converting that floor to a native, hole-less DB.Floor would silently destroy geometry
  // that its existing DirectShape mesh already had correct (the openings baked into the tessellation).
  // So: if this floor has ANY associated IfcOpeningElement (via hostToOpenings/IfcRelVoidsElement),
  // native conversion only proceeds if EVERY one of them is successfully synthesized as its own cutting
  // Opening object below - if even one fails (e.g. a circular opening, not yet supported), this floor
  // is left completely untouched and keeps falling to its existing, correct DirectShape mesh.
  // Two boundary sources, tried in order - the SECOND is a fallback for exporters that never emit a
  // separate 'FootPrint' representation at all (confirmed for a real Tekla-authored file: a slab whose
  // Body is a clean IfcExtrudedAreaSolid over an IfcArbitraryClosedProfileDef, with no FootPrint item
  // anywhere in its ProductDefinitionShape). The extrusion's own swept profile IS the plan footprint
  // for a simple vertical extrusion - reading it directly recovers the boundary Revit's own exporter
  // never needed this fallback for (it always emits a real FootPrint), so this must never fire for
  // anything that already works today: FootPrint is tried FIRST and unconditionally wins if present.
  //
  // The two sources return points in DIFFERENT local frames, so each carries its own combined
  // placement to transform them with: FootPrint points are relative to the element's own
  // ObjectPlacement directly; extrusion-profile points are relative to the profile's own local space,
  // needing the full element-placement -> extrusion-Position -> profile-Position chain (same
  // composition IfcOpeningExtractor already uses for a rectangular opening's own offset profile).
  private static bool TryResolveFloorBoundary(
    StepGraph graph,
    uint expressId,
    out List<IfcVector3> localPoints,
    out IfcPlacement? combinedPlacement
  )
  {
    localPoints = [];
    combinedPlacement = null;

    if (IfcBoundaryExtractor.TryExtractClosedBoundary(graph, expressId, out var footprintPoints))
    {
      if (
        !IfcPlacementResolver.TryResolveElementPlacement(graph, expressId, out var elementPlacement)
        || elementPlacement is null
      )
      {
        return false;
      }

      localPoints = footprintPoints;
      combinedPlacement = elementPlacement;
      return true;
    }

    if (
      IfcProfileExtractor.TryResolveExtrudedProfile(
        graph,
        expressId,
        out uint profileDefId,
        out double extrusionDepthMm,
        out var extrusionPosition,
        out var extrudedDirection
      )
      && IfcProfileExtractor.TryReadArbitraryClosedProfileBoundary(graph, profileDefId, out var profileBoundaryPoints)
      && IfcPlacementResolver.TryResolveElementPlacement(graph, expressId, out var elementPlacementForProfile)
      && elementPlacementForProfile is not null
    )
    {
      var profilePosition = IfcProfileExtractor.ReadProfilePosition(graph, profileDefId);
      // BUG FIX: the extrusion's own Position is the swept profile's BASE (confirmed against a real
      // Revit-exported base slab/floor: ExtrudedDirection is world +Z, Position sits at the slab's
      // BOTTOM, Position+Depth*Direction at its TOP) - but IfcFloorToContourPlateConverter always
      // treats the given boundary as the TOP face (Position.Depth=BEHIND extends material downward
      // from it), matching what the FootPrint tier above genuinely captures (Revit's FootPrint is
      // drawn at the top, mirrored by this project's own floor extraction). Left unshifted, this tier
      // produced a slab sitting a full thickness too low - silently mismatched against its own
      // openings, whose Z comes from IfcOpeningExtractor's independent (and correct) extraction,
      // producing exactly the "opening Z doesn't match the slab" symptom this fixes. Shift is done in
      // the PROFILE's own local frame (not world space) to match localPoints'/combinedPlacement's
      // existing contract - safe because profilePosition (an in-plane 2D placement, see
      // IfcPlacement.FromAxis2Placement2D) never rotates Z, so profile-local Z and extrusion-local Z
      // are the same axis and a pure-Z shift commutes through it either side.
      localPoints = profileBoundaryPoints.Select(p => p + (extrudedDirection * extrusionDepthMm)).ToList();
      combinedPlacement = elementPlacementForProfile.Compose(extrusionPosition).Compose(profilePosition);
      return true;
    }

    // Third and last fallback: no FootPrint, no extrudable profile either - confirmed necessary for a
    // real ArchiCAD-exported (Reference View) floor slab whose only geometry is a raw Tessellation
    // mesh Body. The mesh's own largest horizontal face IS the footprint (see
    // IfcMeshBoundaryExtractor's remarks) - its points are local to the Body's own representation,
    // which is local to the element's OWN placement directly (no extrusion/profile Position
    // composition needed here, unlike the profile-boundary case above).
    if (
      IfcMeshBoundaryExtractor.TryExtractLargestHorizontalFaceBoundary(graph, expressId, out var meshBoundaryPoints)
      && IfcPlacementResolver.TryResolveElementPlacement(graph, expressId, out var elementPlacementForMesh)
      && elementPlacementForMesh is not null
    )
    {
      localPoints = meshBoundaryPoints;
      combinedPlacement = elementPlacementForMesh;
      return true;
    }

    return false;
  }

  private bool TryEnrichFloor(
    DataObject dataObject,
    StepGraph graph,
    IReadOnlyDictionary<string, uint> globalIdToExpressId,
    IReadOnlyDictionary<uint, uint> elementToMaterial,
    IReadOnlyDictionary<uint, List<uint>> hostToOpenings,
    List<DataObject> synthesizedOpenings,
    double lengthScaleToMm
  )
  {
    if (dataObject.applicationId is not { } globalId || !globalIdToExpressId.TryGetValue(globalId, out uint expressId))
    {
      return false;
    }

    if (
      dataObject["ifcType"] is not string ifcType
      || !string.Equals(ifcType, "IfcSlab", StringComparison.OrdinalIgnoreCase)
    )
    {
      return false;
    }

    // BUG FIX (found from a real ArchiCAD-exported file): a genuinely floor-like slab can carry
    // PredefinedType .NOTDEFINED. instead of .FLOOR. explicitly (same "unspecified is not the same as
    // known to be something else" reasoning already applied to footings - see TryEnrichFooting's own
    // remarks). Safe to accept here: .ROOF. and .BASESLAB. are their own distinct, unambiguous
    // predefined types (never NOTDEFINED), so this can't misclassify either of those - the only real
    // risk is a genuinely different .NOTDEFINED. slab (e.g. a landing) getting treated as a floor,
    // accepted as a reasonable trade-off given the alternative (silently staying DirectShape) is worse
    // for the common case this was found from.
    if (
      !graph.Lookup.TryGetValue(expressId, out var node)
      || node.Entity.Count <= 8
      || node.Entity[8] is not StepSymbol predefinedType
      || !(
        predefinedType.Name.ToString() == "FLOOR"
        || predefinedType.Name.ToString().Equals("NOTDEFINED", StringComparison.OrdinalIgnoreCase)
      )
    )
    {
      return false;
    }

    if (!TryResolveFloorBoundary(graph, expressId, out var localPoints, out var placement))
    {
      return false;
    }

    var pendingOpenings = new List<DataObject>();
    if (hostToOpenings.TryGetValue(expressId, out var openingExpressIds))
    {
      foreach (uint openingExpressId in openingExpressIds)
      {
        if (!TryBuildOpeningDataObject(graph, openingExpressId, globalId, lengthScaleToMm, out var openingDataObject))
        {
          logger.LogWarning(
            "TryEnrichFloor: applicationId={ApplicationId} expressId=#{ExpressId} has an associated opening #{OpeningExpressId} that couldn't be extracted (e.g. non-rectangular) - leaving this floor as DirectShape rather than risk losing its openings.",
            dataObject.applicationId,
            expressId,
            openingExpressId
          );
          return false;
        }
        pendingOpenings.Add(openingDataObject!);
      }
    }

    var worldValues = new List<double>(localPoints.Count * 3);
    foreach (var localPoint in localPoints)
    {
      var worldPoint = placement!.TransformPoint(localPoint);
      worldValues.Add(worldPoint.X * lengthScaleToMm);
      worldValues.Add(worldPoint.Y * lengthScaleToMm);
      worldValues.Add(worldPoint.Z * lengthScaleToMm);
    }

    dataObject["builtInCategory"] = "OST_Floors";
    dataObject["location"] = new SOG.Polyline
    {
      value = worldValues,
      closed = true,
      units = "mm",
    };

    if (TryReadObjectType(graph, expressId, out string? floorTypeName))
    {
      dataObject["type"] = floorTypeName;
    }

    if (
      IfcMaterialThicknessExtractor.TryGetLayerSetThicknessMm(
        graph,
        elementToMaterial,
        expressId,
        out double thicknessMm
      )
    )
    {
      // "PL{thickness}" - see FloorToHostConverter.TryParsePlateProfileMm (Tekla plate convention).
      dataObject["profile"] = FormattableString.Invariant($"PL{thicknessMm * lengthScaleToMm:0.###}");
    }

    synthesizedOpenings.AddRange(pendingOpenings);
    if (pendingOpenings.Count > 0)
    {
      logger.LogInformation(
        "TryEnrichFloor: applicationId={ApplicationId} expressId=#{ExpressId} - synthesized {Count} opening(s) to cut into this floor.",
        dataObject.applicationId,
        expressId,
        pendingOpenings.Count
      );
    }

    return true;
  }

  // IfcSlab's .BASESLAB. predefined type covers TWO materially different things, confirmed from a live
  // receive on a 299-slab model with zero IfcFooting entities: 294 were .BASESLAB. (only 5 .FLOOR.), and
  // of those, 78 turned out to be rectangular "Pile Cap" isolated footings (SweptSolid Body, no
  // FootPrint at all - same shape TryEnrichFooting extracts, just never triggered since these are
  // IfcSlab not IfcFooting) and 216 were cylindrical "Pile" foundations (AdvancedBrep Body, no
  // FootPrint, no extrudable profile - same dead end as a beam's mitered-end AdvancedBrep body; not yet
  // recoverable, left as DirectShape rather than guessed). So this tries the SAME flat-mat boundary
  // chain TryEnrichFloor uses (TryResolveFloorBoundary: FootPrint, then the extrusion's own swept
  // profile boundary, then a mesh's largest horizontal face - mirrors TryEnrichFloor exactly, including
  // its opening safety gate, a foundation mat with pile/pier voids having the same CSG-body risk a
  // floor does), then falls back to the rectangular-pad-footing-from-Body shape (shared with
  // TryEnrichFooting) for isolated footings/pile caps that have no boundary of any kind, just a small
  // rectangular thickness extrusion.
  //
  // BUG FIX: previously only tried the bare FootPrint representation (IfcBoundaryExtractor.
  // TryExtractClosedBoundary) - not the profile/mesh fallback tiers TryEnrichFloor already had. Found
  // from a live receive: a real Revit-exported base slab with rounded corners AND openings baked
  // directly into its Body as an IfcArbitraryProfileDefWithVoids (no separate FootPrint at all) fell
  // all the way through to the isolated-pad/pile fallback instead, silently converting a real flat mat
  // into a single Beam/Pile segment - wrong shape entirely (see TryReadArbitraryClosedProfileBoundary's
  // own fix for the WITHVOIDS variant, which this depends on).
  private bool TryEnrichFoundationSlab(
    DataObject dataObject,
    StepGraph graph,
    IReadOnlyDictionary<string, uint> globalIdToExpressId,
    IReadOnlyDictionary<uint, List<uint>> hostToOpenings,
    List<DataObject> synthesizedOpenings,
    double lengthScaleToMm
  )
  {
    if (dataObject.applicationId is not { } globalId || !globalIdToExpressId.TryGetValue(globalId, out uint expressId))
    {
      return false;
    }

    if (
      dataObject["ifcType"] is not string ifcType
      || !string.Equals(ifcType, "IfcSlab", StringComparison.OrdinalIgnoreCase)
    )
    {
      return false;
    }

    if (
      !graph.Lookup.TryGetValue(expressId, out var node)
      || node.Entity.Count <= 8
      || node.Entity[8] is not StepSymbol predefinedType
      || predefinedType.Name.ToString() != "BASESLAB"
    )
    {
      return false;
    }

    if (!TryResolveFloorBoundary(graph, expressId, out var localPoints, out var placement) || placement is null)
    {
      // Not a flat mat at all (no FootPrint, no extrudable-profile boundary, no mesh face either) -
      // try the isolated-pad-footing-from-Body shape instead (covers "Pile Cap" style entries).
      // Deliberately NOT gated by hostToOpenings/opening synthesis - an isolated footing has no
      // shaft-opening concept the way a floor/mat does.
      return TryEnrichRectangularPadFootingFromBody(dataObject, graph, expressId, lengthScaleToMm)
        || TryEnrichPileFromPlacement(dataObject, graph, expressId, lengthScaleToMm);
    }

    var pendingOpenings = new List<DataObject>();
    if (hostToOpenings.TryGetValue(expressId, out var openingExpressIds))
    {
      foreach (uint openingExpressId in openingExpressIds)
      {
        if (!TryBuildOpeningDataObject(graph, openingExpressId, globalId, lengthScaleToMm, out var openingDataObject))
        {
          logger.LogWarning(
            "TryEnrichFoundationSlab: applicationId={ApplicationId} expressId=#{ExpressId} has an associated opening #{OpeningExpressId} that couldn't be extracted (e.g. non-rectangular) - leaving this foundation slab as DirectShape rather than risk losing its openings.",
            dataObject.applicationId,
            expressId,
            openingExpressId
          );
          return false;
        }
        pendingOpenings.Add(openingDataObject!);
      }
    }

    var worldValues = new List<double>(localPoints.Count * 3);
    foreach (var localPoint in localPoints)
    {
      var worldPoint = placement.TransformPoint(localPoint);
      worldValues.Add(worldPoint.X * lengthScaleToMm);
      worldValues.Add(worldPoint.Y * lengthScaleToMm);
      worldValues.Add(worldPoint.Z * lengthScaleToMm);
    }

    dataObject["builtInCategory"] = "OST_StructuralFoundation";
    dataObject["location"] = new SOG.Polyline
    {
      value = worldValues,
      closed = true,
      units = "mm",
    };

    if (TryReadObjectType(graph, expressId, out string? slabTypeName))
    {
      dataObject["type"] = slabTypeName;
    }

    synthesizedOpenings.AddRange(pendingOpenings);
    if (pendingOpenings.Count > 0)
    {
      logger.LogInformation(
        "TryEnrichFoundationSlab: applicationId={ApplicationId} expressId=#{ExpressId} - synthesized {Count} opening(s) to cut into this foundation slab.",
        dataObject.applicationId,
        expressId,
        pendingOpenings.Count
      );
    }

    return true;
  }

  // Unlike every other enriched element, an IfcOpeningElement has NO corresponding Speckle DataObject
  // at all to enrich - confirmed from a live receive: the production IFC importer never uploads
  // IfcOpeningElement as its own object (0 candidates seen, despite openings clearly present in the
  // raw IFC file and referenced via IfcRelVoidsElement). So this builds a brand-new DataObject purely
  // from IFC data, for the caller to inject into the received tree (see EnrichFromFile) - the ONLY
  // place in this feature that creates a new object rather than enriching an existing one.
  //
  // Reuses the existing, already-shipped Opening mechanism (see OpeningToHostConverter, built for
  // Tekla) rather than inventing multi-loop plumbing in FloorToHostConverter/WallToHostConverter: the
  // synthesized Opening is cut into its host via a captured "parentApplicationId" (matched to the
  // host's own applicationId, i.e. its GlobalId) at BAKE time - RevitHostObjectBuilder.BakeObjects
  // already orders objects with a parentApplicationId to bake AFTER everything else, so the host is
  // guaranteed to exist in the receive cache first.
  private static bool TryBuildOpeningDataObject(
    StepGraph graph,
    uint openingExpressId,
    string hostGlobalId,
    double lengthScaleToMm,
    out DataObject? openingDataObject
  )
  {
    openingDataObject = null;

    if (!IfcOpeningExtractor.TryExtractBoundary(graph, openingExpressId, out var worldPoints))
    {
      // e.g. an ellipse/spline profile - not yet supported, never guessed.
      return false;
    }

    if (!TryReadGlobalId(graph, openingExpressId, out string? openingGlobalId) || openingGlobalId is null)
    {
      return false;
    }

    var worldValues = new List<double>(worldPoints.Count * 3);
    foreach (var point in worldPoints)
    {
      worldValues.Add(point.X * lengthScaleToMm);
      worldValues.Add(point.Y * lengthScaleToMm);
      worldValues.Add(point.Z * lengthScaleToMm);
    }

    openingDataObject = new DataObject
    {
      name = "Opening",
      displayValue = [],
      properties = new Dictionary<string, object?> { ["parentApplicationId"] = hostGlobalId },
      applicationId = openingGlobalId,
      // BUG FIX (found from a live receive - crashed the whole receive operation, not just this
      // opening): Base.id is normally computed by serialization, which this brand-new, never-
      // deserialized object never went through, so it stays null. ReceiveConversionResult's
      // constructor requires source.id to be non-null when reporting a successful conversion -
      // reusing the GlobalId (already unique, already used as applicationId) is the same fallback
      // pattern every converter's own `target.applicationId ?? target.id.NotNull()` already relies on,
      // just applied here instead of left to default. GetId() (the "real" way to compute this) fully
      // re-serializes the object and is explicitly documented as expensive - unnecessary just to
      // satisfy a non-null check.
      id = openingGlobalId,
    };
    // Plain "Opening" (not "OST_ShaftOpening") - RevitRootToHostConverter.TryNativeRevitConvert
    // dispatches ANY builtInCategory containing "Opening" to OpeningToHostConverter, and
    // "OST_ShaftOpening" specifically triggers the level-constrained CreateShaftOpening path (which
    // this data doesn't have) rather than the host-based CreateHostedOpening path this needs.
    openingDataObject["builtInCategory"] = "Opening";
    openingDataObject["location"] = new SOG.Polyline
    {
      value = worldValues,
      closed = true,
      units = "mm",
    };

    return true;
  }

  // IfcElementAssembly (e.g. Revit's "Trägersystem"/beam-grid framing assembly) is a pure grouping
  // container with no independent geometry of its own - its member elements (beams, in the real file
  // this was found from) are separate IfcBeam entities, converted individually through the normal
  // beam path elsewhere. Left completely unhandled, it falls all the way through to the generic/
  // DirectShape path, which throws ConversionException("No displayable geometry found") since it has
  // no displayValue mesh - showing up as a spurious conversion ERROR in the receive report for
  // something that was never meant to become its own element. Marks it with a sentinel
  // builtInCategory instead so TeklaRootToHostConverter can treat it as an intentional skip, mirroring
  // exactly how a RevitObject's OST_StructuralFramingSystem is already skipped (see that dispatch).
  private static bool TryEnrichElementAssembly(DataObject dataObject)
  {
    if (
      dataObject["ifcType"] is not string ifcType
      || !string.Equals(ifcType, "IfcElementAssembly", StringComparison.OrdinalIgnoreCase)
    )
    {
      return false;
    }

    dataObject["builtInCategory"] = "IfcElementAssembly";
    return true;
  }

  // An IfcGrid entity bundles MANY axis lines (UAxes+VAxes) under one GlobalId/DataObject, but
  // GridToHostConverter (like every other native converter here) is a strict 1:1 Base -> DB.Element
  // mapping - one DataObject can only ever become ONE DB.Grid. Confirmed from a live receive that the
  // grid's own DataObject already carries a 'FootPrint'/GeometricCurveSet display mesh of ALL its axis
  // lines - so simply enriching it in place with just the FIRST axis and ALSO synthesizing new sibling
  // objects for the rest would leave that original mesh dangling unused (harmless, since a successfully
  // enriched object's displayValue is never consulted again - the dispatch routes straight to
  // GridToHostConverter, never falling through to the generic DirectShape/mesh path). Reuses the
  // received DataObject itself for the first axis (avoiding one redundant synthesized object) and
  // synthesizes new ones (same mechanism as TryBuildOpeningDataObject - no host/parentApplicationId
  // needed here, grid lines are independent top-level elements) for every axis after that.
  private static bool TryEnrichGrid(
    DataObject dataObject,
    StepGraph graph,
    IReadOnlyDictionary<string, uint> globalIdToExpressId,
    List<DataObject> synthesizedGrids,
    double lengthScaleToMm
  )
  {
    if (dataObject.applicationId is not { } globalId || !globalIdToExpressId.TryGetValue(globalId, out uint expressId))
    {
      return false;
    }

    if (
      dataObject["ifcType"] is not string ifcType
      || !string.Equals(ifcType, "IfcGrid", StringComparison.OrdinalIgnoreCase)
    )
    {
      return false;
    }

    if (!IfcGridExtractor.TryExtractAxes(graph, expressId, out var axes))
    {
      return false;
    }

    if (!IfcPlacementResolver.TryResolveElementPlacement(graph, expressId, out var placement) || placement is null)
    {
      return false;
    }

    bool isFirstAxis = true;
    foreach (var axis in axes)
    {
      var worldStart = placement.TransformPoint(axis.Start);
      var worldEnd = placement.TransformPoint(axis.End);
      var line = new SOG.Line
      {
        start = new SOG.Point(
          worldStart.X * lengthScaleToMm,
          worldStart.Y * lengthScaleToMm,
          worldStart.Z * lengthScaleToMm,
          "mm"
        ),
        end = new SOG.Point(
          worldEnd.X * lengthScaleToMm,
          worldEnd.Y * lengthScaleToMm,
          worldEnd.Z * lengthScaleToMm,
          "mm"
        ),
        units = "mm",
      };

      if (isFirstAxis)
      {
        dataObject["builtInCategory"] = "OST_Grids";
        dataObject["location"] = line;
        dataObject["name"] = axis.Tag;
        isFirstAxis = false;
        continue;
      }

      // Synthetic applicationId/id: an IfcGridAxis has no GlobalId of its own (it isn't an
      // IfcRoot-derived entity) - the grid's own GlobalId plus the axis's express id is a stable,
      // unique-per-receive substitute. id is set explicitly for the same reason
      // TryBuildOpeningDataObject sets it - a never-deserialized object's Base.id stays null otherwise,
      // crashing ReceiveConversionResult's constructor.
      string syntheticId = $"{globalId}-{axis.ExpressId}";
      var gridDataObject = new DataObject
      {
        name = axis.Tag,
        displayValue = [],
        properties = new Dictionary<string, object?>(),
        applicationId = syntheticId,
        id = syntheticId,
      };
      gridDataObject["builtInCategory"] = "OST_Grids";
      gridDataObject["location"] = line;
      gridDataObject["name"] = axis.Tag;
      synthesizedGrids.Add(gridDataObject);
    }

    return true;
  }

  private static bool TryReadGlobalId(StepGraph graph, uint elementId, out string? globalId)
  {
    globalId = null;

    if (
      !graph.Lookup.TryGetValue(elementId, out var node)
      || node.Entity.Count == 0
      || node.Entity[0] is not StepString stepString
    )
    {
      return false;
    }

    globalId = stepString.Value.ToString();
    return !string.IsNullOrEmpty(globalId);
  }

  // ObjectType is IfcRoot-derived products' 5th attribute (GlobalId, OwnerHistory, Name, Description,
  // ObjectType, ObjectPlacement, ...) - same fixed schema position IfcPlacementResolver's
  // OBJECT_PLACEMENT_ATTRIBUTE_INDEX (5) is relative to. Read directly from the STEP graph rather than
  // from the already-uploaded Speckle DataObject's properties dict, since this is simpler and more
  // reliable than depending on the production Python importer's exact property-nesting convention.
  // BUG FIX (found from a live receive against a Tekla-authored .ifc file, not a Revit one - this
  // feature's other extractors were all tuned against Revit's own IFC export conventions until now):
  // Tekla's own IFC exporter leaves ObjectType (attribute index 4) null and puts the profile/type name
  // (e.g. "HEA300", "400*400") in Description (attribute index 3) instead - confirmed directly in the
  // file (every IfcBeam/IfcColumn had ObjectType=$ but Description holding the real designation). Falls
  // back to Description only when ObjectType is missing, so Revit-authored files (which always populate
  // ObjectType) are completely unaffected - this never changes behavior for anything already working.
  private static bool TryReadObjectType(StepGraph graph, uint elementId, out string? objectType)
  {
    objectType = null;

    if (!graph.Lookup.TryGetValue(elementId, out var node) || node.Entity.Count <= 4)
    {
      return false;
    }

    if (node.Entity[4] is StepString objectTypeString && !string.IsNullOrEmpty(objectTypeString.Value.ToString()))
    {
      objectType = objectTypeString.Value.ToString();
      return true;
    }

    if (node.Entity.Count > 3 && node.Entity[3] is StepString descriptionString)
    {
      objectType = descriptionString.Value.ToString();
      return !string.IsNullOrEmpty(objectType);
    }

    return false;
  }

  /// <summary>
  /// General recursive walk of every <see cref="Base"/> reachable from <paramref name="root"/> via any
  /// typed or dynamic member (covers <c>Collection.elements</c>, <c>DataObject["@elements"]</c>,
  /// <c>displayValue</c>, and anything else) - deliberately schema-agnostic rather than hard-coding
  /// which property names to descend into, since this enrichment step runs before the connector's own
  /// unpacking/traversal machinery (see RevitHostObjectBuilder.BuildSync step 1) has run.
  /// </summary>
  private static IEnumerable<Base> TraverseAll(Base root)
  {
    var visited = new HashSet<Base>(BaseReferenceEqualityComparer.Instance);
    var stack = new Stack<Base>();
    stack.Push(root);

    while (stack.Count > 0)
    {
      var current = stack.Pop();
      if (!visited.Add(current))
      {
        continue;
      }

      yield return current;

      foreach (object? value in current.GetMembers().Values)
      {
        switch (value)
        {
          case Base childBase:
            stack.Push(childBase);
            break;
          case System.Collections.IEnumerable enumerable and not string:
            foreach (object? item in enumerable)
            {
              if (item is Base itemBase)
              {
                stack.Push(itemBase);
              }
            }
            break;
        }
      }
    }
  }

  private void TryDeleteTempFile(string filePath)
  {
    try
    {
      File.Delete(filePath);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
      logger.LogWarning(ex, "RevitNativeSchemaEnricher: failed to delete temp file {FilePath}.", filePath);
    }
  }
}

// BCL's System.Collections.Generic.ReferenceEqualityComparer is .NET 5+ only - not available on
// net48 (Tekla2025). This tiny equivalent works identically on every TFM this project targets.
internal sealed class BaseReferenceEqualityComparer : IEqualityComparer<Base>
{
  public static readonly BaseReferenceEqualityComparer Instance = new();

  public bool Equals(Base? x, Base? y) => ReferenceEquals(x, y);

  public int GetHashCode(Base obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}
