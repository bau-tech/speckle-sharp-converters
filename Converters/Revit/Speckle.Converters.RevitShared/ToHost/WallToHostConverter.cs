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

public class WallToHostConverter : ITypedConverter<Base, DB.Element>
{
  // Fallback unconnected height (~3m) used when neither a captured Revit parameter nor a Tekla
  // profile string can supply the wall's height.
  private const double DEFAULT_HEIGHT_FEET = 10;

  private readonly IConverterSettingsStore<RevitConversionSettings> _settingsStore;
  private readonly RevitElementTypeResolver _typeResolver;
  private readonly RevitToHostCacheSingleton _cache;
  private readonly ITypedConverter<ICurve, DB.CurveArray> _curveConverter;
  private readonly RevitExistingWallIndex _existingWallIndex;
  private readonly ILogger<WallToHostConverter> _logger;

  public WallToHostConverter(
    IConverterSettingsStore<RevitConversionSettings> settingsStore,
    RevitElementTypeResolver typeResolver,
    RevitToHostCacheSingleton cache,
    ITypedConverter<ICurve, DB.CurveArray> curveConverter,
    RevitExistingWallIndex existingWallIndex,
    ILogger<WallToHostConverter> logger
  )
  {
    _settingsStore = settingsStore;
    _typeResolver = typeResolver;
    _cache = cache;
    _curveConverter = curveConverter;
    _existingWallIndex = existingWallIndex;
    _logger = logger;
  }

  public DB.Element Convert(Base target)
  {
    var doc = _settingsStore.Current.Document;

    if (target["location"] is not ICurve location)
    {
      throw new ConversionException("Native Wall requires a curve location.");
    }

    DB.CurveArray curveArray = _curveConverter.Convert(location);
    if (curveArray.Size == 0)
    {
      throw new ConversionException("Native Wall location did not produce any curves.");
    }
    DB.Curve curve = curveArray.get_Item(0);

    // A wall whose origin applicationId matches an already-received wall in this document is an
    // update, not a new element - reposition/re-type the existing wall instead of inserting a
    // duplicate. Mirrors the equivalent Beam/Column/Foundation mechanism in StructuralFramingHelper.
    if (
      _existingWallIndex.TryFindExisting(target.applicationId ?? target.id, out DB.Wall? existingWall)
      && existingWall!.IsValidObject
    )
    {
      if (existingWall.Location is DB.LocationCurve locationCurve)
      {
        locationCurve.Curve = curve;
      }
      else
      {
        _logger.LogWarning(
          "WallToHostConverter.Convert: existing wall {ElementId} Location is {LocationType}, not a LocationCurve - geometry update skipped.",
          existingWall.Id,
          existingWall.Location?.GetType().Name ?? "null"
        );
      }

      DB.WallType updatedType = ResolveWallType(target);
      if (existingWall.WallType.Id != updatedType.Id)
      {
        existingWall.WallType = updatedType;
        _logger.LogInformation(
          "WallToHostConverter.Convert: swapped wall {ElementId} to type {TypeName}",
          existingWall.Id,
          updatedType.Name
        );
      }

      string updateCacheKey = target.applicationId ?? target.id.NotNull();
      _cache.ReceivedElementsByApplicationId[updateCacheKey] = existingWall;
      OriginApplicationIdSchema.TrySet(existingWall, updateCacheKey, _logger);
      _logger.LogInformation(
        "WallToHostConverter.Convert: UPDATED existing wall {ElementId} for applicationId={ApplicationId}",
        existingWall.Id,
        updateCacheKey
      );
      return existingWall;
    }

    DB.WallType wallType = ResolveWallType(target);
    DB.Level level = ResolveLevel(target, curve);
    double height = ResolveHeightFeet(target);

    double baseOffset = RevitElementPropertyApplicator.TryGetLengthInFeet(
      target,
      "Instance Parameters",
      "WALL_BASE_OFFSET",
      out double parsedBaseOffset
    )
      ? parsedBaseOffset
      : 0;

    DB.Wall wall = DB.Wall.Create(doc, curve, wallType.Id, level.Id, height, baseOffset, false, false);

    ApplyTopConstraint(target, wall);

    if (
      RevitElementPropertyApplicator.TryGetLengthInFeet(target, "Instance Parameters", "WALL_TOP_OFFSET", out double topOffset)
    )
    {
      RevitElementPropertyApplicator.TrySetDouble(wall, DB.BuiltInParameter.WALL_TOP_OFFSET, topOffset);
    }

    string cacheKey = target.applicationId ?? target.id.NotNull();
    _cache.ReceivedElementsByApplicationId[cacheKey] = wall;
    OriginApplicationIdSchema.TrySet(wall, cacheKey, _logger);
    _logger.LogInformation(
      "WallToHostConverter.Convert: CREATED new wall {ElementId} for applicationId={ApplicationId}",
      wall.Id,
      cacheKey
    );

    return wall;
  }

  /// <summary>
  /// Resolves the WallType: for a wall whose profile string parses (Tekla-origin, "{height}*{thickness}"
  /// - see RevitWallToTeklaBeamConverter), prefers a basic WallType whose own <c>Width</c> already
  /// matches the requested thickness - unlike an isolated-foundation family's thickness (an
  /// arbitrarily-named type parameter), <c>WallType.Width</c> is a real built-in API property, so this
  /// match is reliable with no parameter-name guessing involved. Falls back to name/first-available
  /// (the pre-existing behavior) when no profile is present or no width match exists.
  /// </summary>
  private DB.WallType ResolveWallType(Base target)
  {
    if (
      StructuralFramingHelper.TryParseRectangularProfileMm(target["profile"] as string, out double thicknessMm, out _)
      && _typeResolver.FindWallTypeByWidth(thicknessMm) is { } matchByWidth
    )
    {
      return matchByWidth;
    }

    return _typeResolver.FindWallType(target["type"] as string)
      ?? throw new ConversionException("No WallTypes found in the document.");
  }

  /// <summary>
  /// Resolves the wall's height: a captured Revit "Unconnected Height" instance parameter wins if
  /// present (existing Revit round-trip behavior, unchanged), otherwise (always true for Tekla-origin
  /// walls, which carry no such captured parameter) falls back to the height encoded in the wall's own
  /// "{height}*{thickness}" profile string, then the hardcoded default.
  /// </summary>
  private double ResolveHeightFeet(Base target)
  {
    if (
      RevitElementPropertyApplicator.TryGetLengthInFeet(target, "Instance Parameters", "WALL_USER_HEIGHT_PARAM", out double parsedHeight)
    )
    {
      return parsedHeight;
    }

    if (StructuralFramingHelper.TryParseRectangularProfileMm(target["profile"] as string, out _, out double heightMm))
    {
      return DB.UnitUtils.ConvertToInternalUnits(heightMm, DB.UnitTypeId.Millimeters);
    }

    return DEFAULT_HEIGHT_FEET;
  }

  /// <summary>
  /// Resolves the Base Level: a captured Revit level name wins if present, otherwise (always true for
  /// Tekla-origin walls) the level is derived from the wall's own lowest Z coordinate - see
  /// StructuralFramingHelper.ResolveLevel for why this matters (picking the document's lowest level
  /// unconditionally is wrong for anything not actually sitting at that level's elevation).
  /// </summary>
  private DB.Level ResolveLevel(Base target, DB.Curve curve)
  {
    string? levelName = target["level"] as string;
    if (!string.IsNullOrEmpty(levelName))
    {
      return _typeResolver.FindLevel(levelName) ?? throw new ConversionException("No levels found in the document.");
    }

    double zFeet = Math.Min(curve.GetEndPoint(0).Z, curve.GetEndPoint(1).Z);
    return _typeResolver.FindLevelNear(zFeet) ?? throw new ConversionException("No levels found in the document.");
  }

  // "Top Constraint" - if the source wall referenced a level, resolve it in the receiving document
  // and apply it; an "Unconnected" source wall (no value captured) is left as-is.
  private void ApplyTopConstraint(Base target, DB.Wall wall)
  {
    if (
      !RevitElementPropertyApplicator.TryGetLevelReferenceName(
        target,
        "Instance Parameters",
        "WALL_HEIGHT_TYPE",
        out string? topLevelName
      )
      || topLevelName is null
    )
    {
      return;
    }

    DB.Level? topLevel = _typeResolver.FindLevel(topLevelName);
    if (topLevel is not null)
    {
      RevitElementPropertyApplicator.TrySetElementId(wall, DB.BuiltInParameter.WALL_HEIGHT_TYPE, topLevel.Id);
    }
  }

  public object Convert(object target) => Convert((Base)target);
}
