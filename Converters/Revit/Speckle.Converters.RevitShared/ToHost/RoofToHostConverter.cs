using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Converters.RevitShared.Helpers;
using Speckle.Converters.RevitShared.Settings;
using Speckle.Objects;
using Speckle.Sdk.Common;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.RevitShared.ToHost;

public class RoofToHostConverter : ITypedConverter<Base, DB.Element>
{
  private readonly IConverterSettingsStore<RevitConversionSettings> _settingsStore;
  private readonly RevitElementTypeResolver _typeResolver;
  private readonly RevitToHostCacheSingleton _cache;
  private readonly ITypedConverter<ICurve, DB.CurveArray> _curveConverter;

  public RoofToHostConverter(
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
      throw new ConversionException("Native Roof requires a curve boundary location.");
    }

    DB.CurveArray curveArray = _curveConverter.Convert(location);
    if (curveArray.Size == 0)
    {
      throw new ConversionException("Native Roof location did not produce any curves.");
    }

    DB.RoofType roofType =
      _typeResolver.FindRoofType(target["type"] as string)
      ?? throw new ConversionException("No RoofTypes found in the document.");

    DB.Level level =
      _typeResolver.FindLevel(target["level"] as string)
      ?? throw new ConversionException("No levels found in the document.");

    DB.FootPrintRoof roof = doc.Create.NewFootPrintRoof(curveArray, level, roofType, out _);

    string cacheKey = target.applicationId ?? target.id.NotNull();
    _cache.ReceivedElementsByApplicationId[cacheKey] = roof;

    return roof;
  }

  public object Convert(object target) => Convert((Base)target);
}
