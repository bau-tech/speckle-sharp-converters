using System.Globalization;
using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Converters.RevitShared.Helpers;
using Speckle.Converters.RevitShared.Helpers.ProfileMapping;
using Speckle.Converters.RevitShared.Settings;
using Speckle.Sdk.Common;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.RevitShared.ToHost;

/// <summary>
/// Shared FamilySymbol/Level resolution and FamilyInstance creation logic for structural framing
/// elements (Beams, Columns, point/line Foundations) received as native Revit elements.
/// </summary>
public class StructuralFramingHelper
{
  private readonly IConverterSettingsStore<RevitConversionSettings> _settingsStore;
  private readonly RevitElementTypeResolver _typeResolver;
  private readonly RevitToHostCacheSingleton _cache;
  private readonly ITypedConverter<SOG.Line, DB.Line> _lineConverter;
  private readonly ITypedConverter<SOG.Point, DB.XYZ> _pointConverter;
  private readonly ITypedConverter<SOG.Arc, DB.Arc> _arcConverter;
  private readonly RevitExistingBeamIndex _existingBeamIndex;
  private readonly TeklaProfileMappingProvider _profileMappingProvider;
  private readonly ILogger<StructuralFramingHelper> _logger;

  public StructuralFramingHelper(
    IConverterSettingsStore<RevitConversionSettings> settingsStore,
    RevitElementTypeResolver typeResolver,
    RevitToHostCacheSingleton cache,
    ITypedConverter<SOG.Line, DB.Line> lineConverter,
    ITypedConverter<SOG.Point, DB.XYZ> pointConverter,
    ITypedConverter<SOG.Arc, DB.Arc> arcConverter,
    RevitExistingBeamIndex existingBeamIndex,
    TeklaProfileMappingProvider profileMappingProvider,
    ILogger<StructuralFramingHelper> logger
  )
  {
    _settingsStore = settingsStore;
    _typeResolver = typeResolver;
    _cache = cache;
    _lineConverter = lineConverter;
    _pointConverter = pointConverter;
    _arcConverter = arcConverter;
    _existingBeamIndex = existingBeamIndex;
    _profileMappingProvider = profileMappingProvider;
    _logger = logger;
  }

  /// <summary>
  /// Resolves the FamilySymbol and Level for <paramref name="target"/>, creates a FamilyInstance from
  /// its location (point or line), and registers it in the receive cache for later lookup.
  /// </summary>
  public DB.FamilyInstance Create(Base target, DB.BuiltInCategory category, DB.Structure.StructuralType structuralType)
  {
    var doc = _settingsStore.Current.Document;
    object? targetLocation = target["location"];

    // Tekla represents an isolated pad footing as a vertical "beam", but Revit's Isolated Foundation
    // families are point-placed, not curve-based - creating one via the curve-based NewFamilyInstance
    // overload doesn't throw immediately but produces a broken element (any later access, e.g.
    // stamping the origin-id UDA or even just reading .Id, throws NullReferenceException). Detect a
    // near-vertical line under this category and placement-point it instead, using whichever endpoint
    // has the HIGHER Z as the top - unlike a footing round-tripped through RevitFoundationToTeklaConverter.
    // ConvertPadFooting (which always emits top-then-bottom), a footing authored NATIVELY in Tekla has
    // no guaranteed Start/End order, so picking .start unconditionally previously placed some footings
    // at their BOTTOM elevation instead of their top (observed: resolving to the wrong Level as a
    // result). This makes every downstream check (match-by-location, create, reposition) uniformly
    // treat it as point-placed, with no special-casing needed elsewhere in this method.
    if (
      category == DB.BuiltInCategory.OST_StructuralFoundation
      && targetLocation is SOG.Line footingLine
      && IsVerticalLine(footingLine)
    )
    {
      targetLocation = footingLine.start.z >= footingLine.end.z ? footingLine.start : footingLine.end;
    }

    _logger.LogInformation(
      "StructuralFramingHelper.Create: category={Category} applicationId={ApplicationId} id={Id} locationType={LocationType}",
      category,
      target.applicationId,
      target.id,
      targetLocation?.GetType().Name ?? "null"
    );

    // Beams, Columns, and point/line Foundations whose origin applicationId matches an
    // already-received instance in this document are updates, not new elements - reposition the
    // existing instance instead of creating a duplicate. Mirrors the equivalent Tekla-side mechanism
    // (TeklaExistingBeamIndex). These are exactly the 3 categories this method is ever asked to
    // create (see BeamToHostConverter/ColumnToHostConverter/FoundationToHostConverter).
    if (
      IsUpdatableCategory(category)
      && targetLocation is SOG.Line or SOG.Point or SOG.Arc
      && _existingBeamIndex.TryFindExisting(target.applicationId ?? target.id, out DB.FamilyInstance? existingInstance)
      && TryUpdateExisting(target, category, targetLocation, existingInstance!) is { } updatedInstance
    )
    {
      return updatedInstance;
    }

    DB.FamilySymbol symbol =
      ResolveSymbol(target, category)
      ?? throw new ConversionException($"No FamilySymbol found for category '{category}' in the document.");

    DB.Level level =
      ResolveLevel(target, targetLocation) ?? throw new ConversionException("No levels found in the document.");

    DB.FamilyInstance instance = targetLocation switch
    {
      SOG.Line line => doc.Create.NewFamilyInstance(_lineConverter.Convert(line), symbol, level, structuralType),
      SOG.Arc arc => doc.Create.NewFamilyInstance(_arcConverter.Convert(arc), symbol, level, structuralType),
      SOG.Point point => doc.Create.NewFamilyInstance(_pointConverter.Convert(point), symbol, level, structuralType),
      _ => throw new ConversionException($"Native {structuralType} requires a line, arc, or point location."),
    };

    if (
      RevitElementPropertyApplicator.TryGetAngleInRadians(
        target,
        "Instance Parameters",
        "STRUCTURAL_BEND_DIR_ANGLE",
        out double rotation
      )
    )
    {
      // A round-tripped Revit-origin element (its own "STRUCTURAL_BEND_DIR_ANGLE" Instance Parameter was
      // captured on the Tekla send side) - reapply verbatim.
      RevitElementPropertyApplicator.TrySetDouble(instance, DB.BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE, rotation);
    }
    else if (
      RevitElementPropertyApplicator.TryToDouble(target["position_rotation_offset"], out double rotationOffsetDegrees)
      && rotationOffsetDegrees != 0
    )
    {
      // A genuinely Tekla-authored element has no Revit "STRUCTURAL_BEND_DIR_ANGLE" to reapply - fall back
      // to Tekla's own native rotation value, Position.RotationOffset (degrees), captured unconditionally
      // for every part by ClassPropertyExtractor. Every element this codebase writes TO Tekla fixes
      // Position.Rotation to TOP and puts the entire angle into RotationOffset (see
      // RevitColumnBeamToTeklaBeamConverter), so treating RotationOffset as the complete rotation and
      // ignoring the Position.Rotation enum reproduces those round-tripped angles exactly; a genuinely
      // Tekla-native part whose modeler instead chose a non-TOP Position.Rotation (FRONT/BEHIND/LEFT/
      // RIGHT/BELOW) as its reference face will only get this offset applied, not that enum's implied
      // base rotation - no mapping for it exists yet. Sign-flipped: Revit and Tekla rotate in opposite
      // directions (same convention verified in RevitColumnBeamToTeklaBeamConverter.Convert).
      double rotationOffsetRadians = -(rotationOffsetDegrees * Math.PI / 180.0);
      RevitElementPropertyApplicator.TrySetDouble(
        instance,
        DB.BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE,
        rotationOffsetRadians
      );
    }

    // A freshly-created FamilyInstance can throw from Element.SetEntity() below if it hasn't been
    // regenerated yet - observed specifically for Structural Foundation instances (Beams/Columns
    // didn't need this), presumably because footing-category instances resolve some geometry lazily.
    doc.Regenerate();

    string cacheKey = target.applicationId ?? target.id.NotNull();
    _cache.ReceivedElementsByApplicationId[cacheKey] = instance;
    OriginApplicationIdSchema.TrySet(instance, cacheKey, _logger);
    _logger.LogInformation(
      "StructuralFramingHelper.Create: CREATED new instance {ElementId} for applicationId={ApplicationId}",
      instance.Id,
      cacheKey
    );

    return instance;
  }

  private static bool IsUpdatableCategory(DB.BuiltInCategory category) =>
    category == DB.BuiltInCategory.OST_StructuralFraming
    || category == DB.BuiltInCategory.OST_StructuralColumns
    || category == DB.BuiltInCategory.OST_StructuralFoundation;

  // A pad footing's "beam" runs top-to-bottom (near-vertical); a strip/wall footing's runs along the
  // ground (near-horizontal). Dominant Z-extent over X/Y distinguishes the two.
  private static bool IsVerticalLine(SOG.Line line)
  {
    double dx = Math.Abs(line.end.x - line.start.x);
    double dy = Math.Abs(line.end.y - line.start.y);
    double dz = Math.Abs(line.end.z - line.start.z);
    return dz > dx && dz > dy;
  }

  /// <summary>
  /// Repositions/re-symbols <paramref name="existingInstance"/> in place for a matched update, or
  /// returns null if the cached reference went stale before we reached it (e.g. Revit
  /// auto-adjusting/un-joining this instance as a side effect of regenerating a DIFFERENT instance
  /// repositioned earlier in this same receive) - callers fall through to normal creation in that case.
  /// </summary>
  private DB.FamilyInstance? TryUpdateExisting(
    Base target,
    DB.BuiltInCategory category,
    object? targetLocation,
    DB.FamilyInstance existingInstance
  )
  {
    if (!existingInstance.IsValidObject)
    {
      _logger.LogInformation(
        "StructuralFramingHelper.Create: matched instance for applicationId={ApplicationId} is no longer valid; creating new instead.",
        target.applicationId ?? target.id
      );
      return null;
    }

    if (
      targetLocation is SOG.Line or SOG.Arc
      && existingInstance.Location is DB.LocationCurve locationCurve
    )
    {
      DB.Curve oldCurve = locationCurve.Curve;
      DB.Curve newCurve = targetLocation switch
      {
        SOG.Line updateLine => _lineConverter.Convert(updateLine),
        SOG.Arc updateArc => _arcConverter.Convert(updateArc),
        _ => throw new ConversionException("Unreachable - gated by the outer type check."),
      };
      locationCurve.Curve = newCurve;
      _logger.LogInformation(
        "StructuralFramingHelper.Create: repositioned instance {ElementId} from ({OldStart} -> {OldEnd}) to ({NewStart} -> {NewEnd})",
        existingInstance.Id,
        oldCurve.GetEndPoint(0),
        oldCurve.GetEndPoint(1),
        newCurve.GetEndPoint(0),
        newCurve.GetEndPoint(1)
      );
    }
    else if (targetLocation is SOG.Point updatePoint && existingInstance.Location is DB.LocationPoint locationPoint)
    {
      DB.XYZ oldPoint = locationPoint.Point;
      DB.XYZ newPoint = _pointConverter.Convert(updatePoint);
      locationPoint.Point = newPoint;
      ReapplyPlacementRotation(target, existingInstance);
      _logger.LogInformation(
        "StructuralFramingHelper.Create: repositioned instance {ElementId} from {OldPoint} to {NewPoint}",
        existingInstance.Id,
        oldPoint,
        newPoint
      );
    }
    else
    {
      _logger.LogWarning(
        "StructuralFramingHelper.Create: instance {ElementId} Location is {LocationType} but incoming location is {TargetLocationType} - geometry update skipped.",
        existingInstance.Id,
        existingInstance.Location?.GetType().Name ?? "null",
        targetLocation?.GetType().Name ?? "null"
      );
    }

    // A profile/cross-section change at the source (e.g. re-sized in Tekla) never reaches an
    // already-existing Revit instance just by repositioning it - the FamilySymbol has to be
    // re-resolved and swapped too, mirroring the new-instance resolution below.
    DB.FamilySymbol? updatedSymbol = ResolveSymbol(target, category);
    if (updatedSymbol is not null && existingInstance.Symbol.Id != updatedSymbol.Id)
    {
      existingInstance.Symbol = updatedSymbol;
      _logger.LogInformation(
        "StructuralFramingHelper.Create: swapped instance {ElementId} to symbol {SymbolName}",
        existingInstance.Id,
        updatedSymbol.Name
      );
    }

    string updateCacheKey = target.applicationId ?? target.id.NotNull();
    _cache.ReceivedElementsByApplicationId[updateCacheKey] = existingInstance;
    // Stamp even a native-UniqueId-matched update (a beam originally authored in Revit, never
    // before "received") so it becomes deletion-tracked from now on: once Speckle has touched it at
    // least once, a future receive that no longer includes it can recognize it as removed at the
    // source. Beams that have never round-tripped are never touched here, so unrelated native Revit
    // content can never become an unintended deletion candidate.
    OriginApplicationIdSchema.TrySet(existingInstance, updateCacheKey, _logger);
    _logger.LogInformation(
      "StructuralFramingHelper.Create: UPDATED existing instance {ElementId} for applicationId={ApplicationId}",
      existingInstance.Id,
      updateCacheKey
    );
    return existingInstance;
  }

  /// <summary>
  /// Resolves the Base Level: a captured Revit level name wins if present, otherwise (always true for
  /// Tekla-origin objects, which have no Revit "Level" concept) the level is derived from the
  /// element's own lowest Z coordinate - NOT unconditionally the document's lowest level, which is
  /// wrong for anything not actually sitting at that level's elevation (observed: Tekla columns
  /// landing 3m off because the document's lowest level wasn't at their true base elevation).
  /// </summary>
  private DB.Level? ResolveLevel(Base target, object? targetLocation)
  {
    string? levelName = target["level"] as string;
    if (!string.IsNullOrEmpty(levelName))
    {
      DB.Level? byName = _typeResolver.FindLevel(levelName);
      _logger.LogInformation(
        "StructuralFramingHelper.ResolveLevel: captured levelName={LevelName} -> {LevelResult}",
        levelName,
        byName is null ? "null" : $"{byName.Name} (elevation={byName.Elevation}ft)"
      );
      return byName;
    }

    double? zFeet = targetLocation switch
    {
      SOG.Point point => _pointConverter.Convert(point).Z,
      SOG.Line line => Math.Min(_pointConverter.Convert(line.start).Z, _pointConverter.Convert(line.end).Z),
      SOG.Arc arc => Math.Min(
        Math.Min(_pointConverter.Convert(arc.startPoint).Z, _pointConverter.Convert(arc.midPoint).Z),
        _pointConverter.Convert(arc.endPoint).Z
      ),
      _ => null,
    };

    DB.Level? level = zFeet is { } z ? _typeResolver.FindLevelNear(z) : _typeResolver.FindLevel(null);
    _logger.LogInformation(
      "StructuralFramingHelper.ResolveLevel: no levelName, zFeet={ZFeet} -> {LevelResult}",
      zFeet,
      level is null ? "null" : $"{level.Name} (elevation={level.Elevation}ft)"
    );
    return level;
  }

  // Spike: Tekla-sourced beams never carry a real family/type ("type" is always the literal string
  // "Beam"), so FindFamilySymbol always falls through to FirstOrDefault(). For rectangular profiles
  // we can do better by synthesizing a correctly-dimensioned type on the fly instead of guessing; for
  // non-rectangular profiles (named catalog sections like "HEA200", round "D300", angles, ...) an
  // explicit user-confirmed mapping (see TeklaProfileMappingProvider/the profile mapping dialog) is
  // the only reliable source, since there's no dimension-based synthesis for those shapes.
  // Shared by both new-instance creation and the matched-update path, so a profile change at the
  // source (e.g. re-sized in Tekla) is reflected on an existing Revit instance too, not just new ones.
  private DB.FamilySymbol? ResolveSymbol(Base target, DB.BuiltInCategory category)
  {
    string? profile = target["profile"] as string;
    string? mappedFamily = null;
    string? mappedType = null;
    bool foundMapping =
      !string.IsNullOrEmpty(profile)
      && _profileMappingProvider.TryGetFamilyType(category, profile!, out mappedFamily, out mappedType);
    _logger.LogInformation(
      "StructuralFramingHelper.ResolveSymbol: category={Category} profile={Profile} foundMapping={FoundMapping} mappedFamily={MappedFamily} mappedType={MappedType}",
      category,
      profile,
      foundMapping,
      mappedFamily,
      mappedType
    );
    if (foundMapping && _typeResolver.FindExactFamilySymbol(mappedFamily!, mappedType!, category) is { } mappedSymbol)
    {
      _logger.LogInformation(
        "StructuralFramingHelper.ResolveSymbol: resolved via mapping to symbol {SymbolName}",
        mappedSymbol.Name
      );
      return mappedSymbol;
    }
    else if (foundMapping)
    {
      _logger.LogWarning(
        "StructuralFramingHelper.ResolveSymbol: mapping found {MappedFamily}:{MappedType} but no exact FamilySymbol match in this document - falling through.",
        mappedFamily,
        mappedType
      );
    }

    // NOT applied to OST_StructuralFoundation: a footing's "{width}*{depth}" profile string encodes
    // its PLAN footprint (see RevitFoundationToTeklaConverter.ApplyCommonProperties/ConvertPadFooting),
    // an entirely different concept from a beam's cross-section width/height - applying this beam
    // heuristic to a footing risks landing the footprint value on whatever parameter this family calls
    // its THICKNESS (if it happens to match one of the "h"/"Height" name candidates), which is wrong by
    // definition since a footing's thickness is never part of its profile string in the first place
    // (Tekla encodes it as the length of the pad footing's own vertical "beam" instead - see
    // FoundationToHostConverter, which applies footing dimensions explicitly after this method returns).
    if (
      (category == DB.BuiltInCategory.OST_StructuralFraming || category == DB.BuiltInCategory.OST_StructuralColumns)
      && TryParseRectangularProfileMm(profile, out double widthMm, out double heightMm)
    )
    {
      DB.FamilySymbol? rectangularSymbol = _typeResolver.FindOrCreateRectangularSymbol(category, widthMm, heightMm);
      if (rectangularSymbol is not null)
      {
        return rectangularSymbol;
      }
    }

    return _typeResolver.FindFamilySymbol(target["family"] as string, target["type"] as string, category);
  }

  // Tekla rectangular profile strings look like "500*300" (height*width, mm) - this is the exact
  // format this codebase already writes on send for rectangular sections
  // (RevitColumnBeamToTeklaBeamConverter.TryMapProfileHeuristic: $"{heightMm}*{widthMm}"), so parsing
  // it back here is just the inverse of that - first number is height, second is width.
  // Public: also used by the receive-time profile mapping dialog (a different assembly) to decide
  // which incoming profiles already auto-resolve and so don't need a dialog row.
  public static bool TryParseRectangularProfileMm(string? profile, out double widthMm, out double heightMm)
  {
    widthMm = 0;
    heightMm = 0;

    if (string.IsNullOrEmpty(profile))
    {
      return false;
    }

    string[] parts = profile.Split('*');
    if (parts.Length != 2)
    {
      return false;
    }

    return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out heightMm)
      && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out widthMm);
  }

  /// <summary>
  /// Re-applies a point-based placement's captured plan rotation (e.g. for structural columns).
  /// Must be called AFTER any level/offset constraints have been applied (e.g.
  /// <c>ColumnToHostConverter.ApplyVerticalExtents</c>) - setting those on a rotated instance can
  /// cause Revit to recompute the LocationPoint and shift the element away from its insertion point,
  /// so rotation needs to be the final placement step.
  /// </summary>
  public void ReapplyPlacementRotation(Base target, DB.FamilyInstance instance)
  {
    if (
      target["location"] is not Base locationBase
      || !RevitElementPropertyApplicator.TryToDouble(locationBase["rotation"], out double placementRotation)
    )
    {
      return;
    }

    _settingsStore.Current.Document.Regenerate();

    if (instance.Location is not DB.LocationPoint locationPoint)
    {
      return;
    }

    // Rotate() applies a delta, but the captured value is an absolute angle - rotate by the
    // difference from the instance's current (freshly-placed) rotation to land on that angle.
    double rotationDelta = placementRotation - locationPoint.Rotation;
    if (rotationDelta != 0)
    {
      using DB.Line axis = DB.Line.CreateBound(locationPoint.Point, locationPoint.Point + DB.XYZ.BasisZ);
      _ = locationPoint.Rotate(axis, rotationDelta);
    }
  }
}
