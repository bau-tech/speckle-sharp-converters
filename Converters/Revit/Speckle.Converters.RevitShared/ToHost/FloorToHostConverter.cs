using System.Globalization;
using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Converters.RevitShared.Helpers;
using Speckle.Converters.RevitShared.Settings;
using Speckle.Objects;
using Speckle.Sdk.Common;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.RevitShared.ToHost;

public class FloorToHostConverter : ITypedConverter<Base, DB.Element>
{
  private readonly IConverterSettingsStore<RevitConversionSettings> _settingsStore;
  private readonly RevitElementTypeResolver _typeResolver;
  private readonly RevitToHostCacheSingleton _cache;
  private readonly ITypedConverter<ICurve, DB.CurveArray> _curveConverter;
  private readonly ITypedConverter<DB.CurveArray, DB.CurveLoop> _curveLoopConverter;
  private readonly RevitExistingFloorIndex _existingFloorIndex;
  private readonly ILogger<FloorToHostConverter> _logger;

  public FloorToHostConverter(
    IConverterSettingsStore<RevitConversionSettings> settingsStore,
    RevitElementTypeResolver typeResolver,
    RevitToHostCacheSingleton cache,
    ITypedConverter<ICurve, DB.CurveArray> curveConverter,
    ITypedConverter<DB.CurveArray, DB.CurveLoop> curveLoopConverter,
    RevitExistingFloorIndex existingFloorIndex,
    ILogger<FloorToHostConverter> logger
  )
  {
    _settingsStore = settingsStore;
    _typeResolver = typeResolver;
    _cache = cache;
    _curveConverter = curveConverter;
    _curveLoopConverter = curveLoopConverter;
    _existingFloorIndex = existingFloorIndex;
    _logger = logger;
  }

  public DB.Element Convert(Base target)
  {
    var doc = _settingsStore.Current.Document;

    if (target["location"] is not ICurve location)
    {
      throw new ConversionException("Native Floor requires a curve boundary location.");
    }

    DB.CurveArray curveArray = _curveConverter.Convert(location);
    if (curveArray.Size == 0)
    {
      throw new ConversionException("Native Floor location did not produce any curves.");
    }

    DB.CurveLoop loop = _curveLoopConverter.Convert(curveArray);
    DB.FloorType floorType = ResolveFloorType(target);
    DB.Level level = ResolveLevel(target, curveArray, out double? computedHeightOffsetFeet);
    string cacheKey = target.applicationId ?? target.id.NotNull();

    // A matched floor whose plan footprint hasn't changed (only level/offset/type) can be updated
    // cheaply in place, preserving its ElementId - so Revit-side tags/schedules/filters referencing
    // it survive a resend. A genuine boundary/shape change still needs delete-and-recreate: Revit's
    // DB.Floor has no simple boundary-edit API the way DB.LocationCurve.Curve gives beams/walls.
    if (
      _existingFloorIndex.TryFindExisting(target.applicationId ?? target.id, out DB.Floor? existingFloor)
      && existingFloor!.IsValidObject
    )
    {
      if (HasSameFootprint(doc, existingFloor, loop))
      {
        UpdateFloorInPlace(existingFloor, target, floorType, level, computedHeightOffsetFeet, cacheKey);
        return existingFloor;
      }

      doc.Delete(existingFloor.Id);
      _logger.LogInformation(
        "FloorToHostConverter.Convert: deleted existing floor {ElementId} for applicationId={ApplicationId} (footprint changed - delete-and-recreate update).",
        existingFloor.Id,
        target.applicationId ?? target.id
      );
    }

    DB.Floor floor = DB.Floor.Create(doc, new List<DB.CurveLoop> { loop }, floorType.Id, level.Id);
    ApplyHeightOffset(floor, target, computedHeightOffsetFeet);

    _cache.ReceivedElementsByApplicationId[cacheKey] = floor;
    OriginApplicationIdSchema.TrySet(floor, cacheKey, _logger);
    _logger.LogInformation(
      "FloorToHostConverter.Convert: CREATED new floor {ElementId} for applicationId={ApplicationId}",
      floor.Id,
      cacheKey
    );

    return floor;
  }

  private void UpdateFloorInPlace(
    DB.Floor existingFloor,
    Base target,
    DB.FloorType floorType,
    DB.Level level,
    double? computedHeightOffsetFeet,
    string cacheKey
  )
  {
    if (existingFloor.GetTypeId() != floorType.Id)
    {
      existingFloor.ChangeTypeId(floorType.Id);
    }

    RevitElementPropertyApplicator.TrySetElementId(existingFloor, DB.BuiltInParameter.LEVEL_PARAM, level.Id);
    ApplyHeightOffset(existingFloor, target, computedHeightOffsetFeet);

    _cache.ReceivedElementsByApplicationId[cacheKey] = existingFloor;
    OriginApplicationIdSchema.TrySet(existingFloor, cacheKey, _logger);
    _logger.LogInformation(
      "FloorToHostConverter.Convert: UPDATED existing floor {ElementId} in place (same footprint) for applicationId={ApplicationId}",
      existingFloor.Id,
      cacheKey
    );
  }

  private static void ApplyHeightOffset(DB.Floor floor, Base target, double? computedHeightOffsetFeet)
  {
    // A captured Revit "Height Offset From Level" instance parameter wins if present (existing
    // Revit round-trip behavior, unchanged). Otherwise (always true for Tekla-origin floors, which
    // carry no such captured parameter) - Floor.Create always places the sketch AT the resolved
    // level's own elevation, discarding the boundary's true Z, so the offset has to be computed
    // and reapplied explicitly here, or the floor silently snaps flush to the nearest level.
    if (
      RevitElementPropertyApplicator.TryGetLengthInFeet(
        target,
        "Instance Parameters",
        "FLOOR_HEIGHTABOVELEVEL_PARAM",
        out double capturedHeightOffset
      )
    )
    {
      RevitElementPropertyApplicator.TrySetDouble(
        floor,
        DB.BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM,
        capturedHeightOffset
      );
    }
    else if (computedHeightOffsetFeet is { } offset)
    {
      RevitElementPropertyApplicator.TrySetDouble(floor, DB.BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM, offset);
    }
  }

  /// <summary>
  /// Compares the new boundary's XY footprint against the existing floor's own current sketch
  /// profile (read via its Sketch element), order/rotation-insensitive - lets a level/offset/type-
  /// only resend update in place instead of delete-and-recreate. Fails closed (returns false,
  /// forcing delete-and-recreate) on any point-count mismatch or if the sketch can't be read, so an
  /// API assumption that turns out wrong just falls back to the already-working behavior.
  /// </summary>
#pragma warning disable CA1031
  private static bool HasSameFootprint(DB.Document doc, DB.Floor existingFloor, DB.CurveLoop newLoop)
  {
    try
    {
      if (doc.GetElement(existingFloor.SketchId) is not DB.Sketch sketch || sketch.Profile.Size == 0)
      {
        return false;
      }

      var existingPoints = new List<DB.XYZ>();
      foreach (DB.CurveArray curveArray in sketch.Profile)
      {
        foreach (DB.Curve curve in curveArray)
        {
          existingPoints.Add(curve.GetEndPoint(0));
        }
      }

      var newPoints = new List<DB.XYZ>();
      foreach (DB.Curve curve in newLoop)
      {
        newPoints.Add(curve.GetEndPoint(0));
      }

      if (existingPoints.Count != newPoints.Count)
      {
        return false;
      }

      const double TOLERANCE_FEET = 0.01; // ~3mm

      var unmatched = new List<DB.XYZ>(existingPoints);
      foreach (DB.XYZ newPoint in newPoints)
      {
        int matchIndex = unmatched.FindIndex(existingPoint =>
          Math.Abs(existingPoint.X - newPoint.X) < TOLERANCE_FEET
          && Math.Abs(existingPoint.Y - newPoint.Y) < TOLERANCE_FEET
        );
        if (matchIndex < 0)
        {
          return false;
        }
        unmatched.RemoveAt(matchIndex);
      }

      return true;
    }
    catch (Exception)
    {
      return false;
    }
  }
#pragma warning restore CA1031

  /// <summary>
  /// Resolves the FloorType: for a floor whose profile string parses as a Tekla plate thickness
  /// (Tekla-origin, "PL{thicknessMm}" - see RevitFloorToContourPlateConverter), prefers a FloorType
  /// whose own compound-structure thickness already matches - reliable, no name-guessing, mirroring
  /// WallToHostConverter's width-based resolution. Falls back to name/first-available otherwise.
  /// </summary>
  private DB.FloorType ResolveFloorType(Base target)
  {
    if (
      TryParsePlateProfileMm(target["profile"] as string, out double thicknessMm)
      && _typeResolver.FindFloorTypeByThickness(thicknessMm) is { } matchByThickness
    )
    {
      return matchByThickness;
    }

    return _typeResolver.FindFloorType(target["type"] as string)
      ?? throw new ConversionException("No FloorTypes found in the document.");
  }

  /// <summary>
  /// Resolves the Level: a captured Revit level name wins if present (no offset computed here - the
  /// existing captured "Height Offset From Level" instance parameter already restores the exact
  /// value in that case). Otherwise (always true for Tekla-origin floors) the level is derived from
  /// the boundary's own Z coordinate via Z-proximity (mirroring WallToHostConverter/
  /// StructuralFramingHelper), and <paramref name="heightOffsetFeet"/> is set to the gap between the
  /// boundary's true Z and the matched level's elevation - <c>DB.Floor.Create</c> always
  /// places the sketch AT the level's own elevation, so this must be reapplied by the caller via
  /// FLOOR_HEIGHTABOVELEVEL_PARAM or the floor silently snaps flush to the nearest level.
  /// </summary>
  private DB.Level ResolveLevel(Base target, DB.CurveArray curveArray, out double? heightOffsetFeet)
  {
    heightOffsetFeet = null;

    string? levelName = target["level"] as string;
    if (!string.IsNullOrEmpty(levelName))
    {
      return _typeResolver.FindLevel(levelName) ?? throw new ConversionException("No levels found in the document.");
    }

    double zFeet = curveArray.get_Item(0).GetEndPoint(0).Z;
    DB.Level level =
      _typeResolver.FindLevelNear(zFeet) ?? throw new ConversionException("No levels found in the document.");
    heightOffsetFeet = zFeet - level.Elevation;
    return level;
  }

  // Tekla plate profile strings look like "PL500" (thickness only, mm) - see
  // RevitFloorToContourPlateConverter's thickness formula on the reverse direction.
  private static bool TryParsePlateProfileMm(string? profile, out double thicknessMm)
  {
    thicknessMm = 0;
    if (string.IsNullOrEmpty(profile) || !profile.StartsWith("PL", StringComparison.OrdinalIgnoreCase))
    {
      return false;
    }

    return double.TryParse(profile.AsSpan(2), NumberStyles.Float, CultureInfo.InvariantCulture, out thicknessMm);
  }

  public object Convert(object target) => Convert((Base)target);
}
