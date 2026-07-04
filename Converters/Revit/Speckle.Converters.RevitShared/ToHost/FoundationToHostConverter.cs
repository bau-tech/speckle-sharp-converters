using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Converters.RevitShared.Helpers;
using Speckle.Converters.RevitShared.Settings;
using Speckle.Objects;
using Speckle.Sdk.Common;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.RevitShared.ToHost;

public class FoundationToHostConverter : ITypedConverter<Base, DB.Element>
{
  private readonly IConverterSettingsStore<RevitConversionSettings> _settingsStore;
  private readonly RevitElementTypeResolver _typeResolver;
  private readonly RevitToHostCacheSingleton _cache;
  private readonly StructuralFramingHelper _structuralFramingHelper;
  private readonly ITypedConverter<ICurve, DB.CurveArray> _curveConverter;
  private readonly ITypedConverter<DB.CurveArray, DB.CurveLoop> _curveLoopConverter;

  public FoundationToHostConverter(
    IConverterSettingsStore<RevitConversionSettings> settingsStore,
    RevitElementTypeResolver typeResolver,
    RevitToHostCacheSingleton cache,
    StructuralFramingHelper structuralFramingHelper,
    ITypedConverter<ICurve, DB.CurveArray> curveConverter,
    ITypedConverter<DB.CurveArray, DB.CurveLoop> curveLoopConverter
  )
  {
    _settingsStore = settingsStore;
    _typeResolver = typeResolver;
    _cache = cache;
    _structuralFramingHelper = structuralFramingHelper;
    _curveConverter = curveConverter;
    _curveLoopConverter = curveLoopConverter;
  }

  public DB.Element Convert(Base target)
  {
    // Wall Foundations (continuous footings hosted on a Wall) are HostObjects, not FamilyInstances -
    // they're created via WallFoundation.Create against the already-received host Wall.
    if (target["location"] is SOG.Line && TryGetHostWall(target) is DB.Wall hostWall)
    {
      return CreateWallFoundation(target, hostWall);
    }

    // Isolated footings (point) and other line-based footings are family instances,
    // created the same way as Beams/Columns. Foundation slabs have a closed boundary instead.
    if (target["location"] is SOG.Point or SOG.Line)
    {
      DB.FamilyInstance footing = _structuralFramingHelper.Create(
        target,
        DB.BuiltInCategory.OST_StructuralFoundation,
        DB.Structure.StructuralType.Footing
      );
      _structuralFramingHelper.ReapplyPlacementRotation(target, footing);
      return footing;
    }

    return CreateFoundationSlab(target);
  }

  /// <summary>
  /// Looks up the host Wall for <paramref name="target"/> via its captured <c>parentApplicationId</c>
  /// (set on send for FamilyInstance/Opening hosts - WallFoundation is hosted on a Wall in the same way).
  /// Returns null if no host was captured, or if the host hasn't been received as a Wall in this operation.
  /// </summary>
  private DB.Wall? TryGetHostWall(Base target) =>
    target["properties"] is Dictionary<string, object?> properties
    && properties.GetOrDefault("parentApplicationId") is string hostApplicationId
    && _cache.ReceivedElementsByApplicationId.TryGetValue(hostApplicationId, out DB.Element? host)
    && host is DB.Wall wall
      ? wall
      : null;

  private DB.WallFoundation CreateWallFoundation(Base target, DB.Wall hostWall)
  {
    var doc = _settingsStore.Current.Document;

    DB.WallFoundationType wallFoundationType =
      _typeResolver.FindWallFoundationType(target["type"] as string)
      ?? throw new ConversionException("No Wall Foundation types found in the document.");

    DB.WallFoundation wallFoundation = DB.WallFoundation.Create(doc, wallFoundationType.Id, hostWall.Id);

    string cacheKey = target.applicationId ?? target.id.NotNull();
    _cache.ReceivedElementsByApplicationId[cacheKey] = wallFoundation;

    return wallFoundation;
  }

  private DB.Floor CreateFoundationSlab(Base target)
  {
    var doc = _settingsStore.Current.Document;

    if (target["location"] is not ICurve location)
    {
      throw new ConversionException("Native Foundation Slab requires a curve boundary location.");
    }

    DB.CurveArray curveArray = _curveConverter.Convert(location);
    if (curveArray.Size == 0)
    {
      throw new ConversionException("Native Foundation Slab location did not produce any curves.");
    }

    DB.CurveLoop loop = _curveLoopConverter.Convert(curveArray);

    DB.FloorType floorType =
      _typeResolver.FindFoundationSlabType(target["type"] as string)
      ?? throw new ConversionException("No foundation slab FloorTypes found in the document.");

    DB.Level level =
      _typeResolver.FindLevel(target["level"] as string) ?? throw new ConversionException("No levels found in the document.");

    DB.Floor floor = DB.Floor.Create(doc, new List<DB.CurveLoop> { loop }, floorType.Id, level.Id);

    if (
      RevitElementPropertyApplicator.TryGetLengthInFeet(
        target,
        "Instance Parameters",
        "FLOOR_HEIGHTABOVELEVEL_PARAM",
        out double heightOffset
      )
    )
    {
      RevitElementPropertyApplicator.TrySetDouble(floor, DB.BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM, heightOffset);
    }

    string cacheKey = target.applicationId ?? target.id.NotNull();
    _cache.ReceivedElementsByApplicationId[cacheKey] = floor;

    return floor;
  }

  public object Convert(object target) => Convert((Base)target);
}
