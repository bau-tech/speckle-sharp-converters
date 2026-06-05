using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Objects.Data;
using Speckle.Sdk.Models;

namespace Speckle.Converters.TeklaShared.ToHost;

public class TeklaRootToHostConverter : IRootToHostConverter
{
  private readonly IConverterSettingsStore<TeklaConversionSettings> _settingsStore;
  private readonly ITypedConverter<TeklaObject, TSM.Beam> _beamConverter;
  private readonly ITypedConverter<Base, TSM.ModelObject> _builtElementConverter;
  private readonly GeometricItemToHostConverter _genericConverter;
  private readonly SubComponentToHostConverter _subComponentConverter;
  private readonly TeklaReceiveCache _receiveCache;

  public TeklaRootToHostConverter(
    IConverterSettingsStore<TeklaConversionSettings> settingsStore,
    ITypedConverter<TeklaObject, TSM.Beam> beamConverter,
    ITypedConverter<Base, TSM.ModelObject> builtElementConverter,
    GeometricItemToHostConverter genericConverter,
    SubComponentToHostConverter subComponentConverter,
    TeklaReceiveCache receiveCache
  )
  {
    _settingsStore = settingsStore;
    _beamConverter = beamConverter;
    _builtElementConverter = builtElementConverter;
    _genericConverter = genericConverter;
    _subComponentConverter = subComponentConverter;
    _receiveCache = receiveCache;
  }

  public object Convert(Base target)
  {
    TSM.ModelObject? result = null;

    if (target is TeklaObject teklaObject)
    {
      // Dispatch based on type
      if (teklaObject.Type == "Beam" || teklaObject.Type == "Column")
      {
        result = _beamConverter.Convert(teklaObject);
      }

      if (result != null)
      {
        _receiveCache.Add(target.id ?? target.applicationId, result);
        return result;
      }
    }

    if (_settingsStore.Current.ReceiveMode == ReceiveMode.Native)
    {
      try
      {
        return _builtElementConverter.Convert(target);
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        // Fallback to generic if native fails
        return _genericConverter.Convert(target);
      }
    }

    return _genericConverter.Convert(target);
  }
}
