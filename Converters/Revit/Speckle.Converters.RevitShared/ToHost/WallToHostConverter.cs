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
  // Fallback unconnected height (~3m) used when "Unconnected Height" can't be resolved from captured parameters.
  private const double DEFAULT_HEIGHT_FEET = 10;

  private readonly IConverterSettingsStore<RevitConversionSettings> _settingsStore;
  private readonly RevitElementTypeResolver _typeResolver;
  private readonly RevitToHostCacheSingleton _cache;
  private readonly ITypedConverter<ICurve, DB.CurveArray> _curveConverter;

  public WallToHostConverter(
    IConverterSettingsStore<RevitConversionSettings> settingsStore,
    RevitElementTypeResolver typeResolver,
    RevitToHostCacheSingleton cache,
    ITypedConverter<ICurve, DB.CurveArray> curveConverter
  )
  {
    _settingsStore = settingsStore;
    _typeResolver = typeResolver;
    _cache = cache;
    _curveConverter = curveConverter;
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

    DB.WallType wallType =
      _typeResolver.FindWallType(target["type"] as string)
      ?? throw new ConversionException("No WallTypes found in the document.");

    DB.Level level =
      _typeResolver.FindLevel(target["level"] as string)
      ?? throw new ConversionException("No levels found in the document.");

    double height = RevitElementPropertyApplicator.TryGetLengthInFeet(
      target,
      "Instance Parameters",
      "WALL_USER_HEIGHT_PARAM",
      out double parsedHeight
    )
      ? parsedHeight
      : DEFAULT_HEIGHT_FEET;

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
      RevitElementPropertyApplicator.TryGetLengthInFeet(
        target,
        "Instance Parameters",
        "WALL_TOP_OFFSET",
        out double topOffset
      )
    )
    {
      RevitElementPropertyApplicator.TrySetDouble(wall, DB.BuiltInParameter.WALL_TOP_OFFSET, topOffset);
    }

    string cacheKey = target.applicationId ?? target.id.NotNull();
    _cache.ReceivedElementsByApplicationId[cacheKey] = wall;

    return wall;
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
