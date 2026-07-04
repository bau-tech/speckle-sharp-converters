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

  public FloorToHostConverter(
    IConverterSettingsStore<RevitConversionSettings> settingsStore,
    RevitElementTypeResolver typeResolver,
    RevitToHostCacheSingleton cache,
    ITypedConverter<ICurve, DB.CurveArray> curveConverter,
    ITypedConverter<DB.CurveArray, DB.CurveLoop> curveLoopConverter
  )
  {
    _settingsStore = settingsStore;
    _typeResolver = typeResolver;
    _cache = cache;
    _curveConverter = curveConverter;
    _curveLoopConverter = curveLoopConverter;
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

    DB.FloorType floorType =
      _typeResolver.FindFloorType(target["type"] as string)
      ?? throw new ConversionException("No FloorTypes found in the document.");

    DB.Level level =
      _typeResolver.FindLevel(target["level"] as string)
      ?? throw new ConversionException("No levels found in the document.");

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
