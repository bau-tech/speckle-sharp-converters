using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Converters.TeklaShared.ToHost.Ifc;
using Speckle.Objects.Data;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.TeklaShared.ToHost;

public class TeklaRootToHostConverter : IRootToHostConverter
{
  private readonly IConverterSettingsStore<TeklaConversionSettings> _settingsStore;
  private readonly ITypedConverter<TeklaObject, TSM.Beam> _beamConverter;
  private readonly ITypedConverter<TeklaObject, TSM.ContourPlate> _contourPlateConverter;
  private readonly ITypedConverter<TeklaObject, TSM.PolyBeam> _polyBeamConverter;
  private readonly ITypedConverter<TeklaObject, TSM.BentPlate> _bentPlateConverter;
  private readonly ITypedConverter<TeklaObject, TSM.SpiralBeam> _spiralBeamConverter;
  private readonly ITypedConverter<TeklaObject, TSM.LoftedPlate> _loftedPlateConverter;
  private readonly ITypedConverter<TeklaObject, TSM.Grid> _gridConverter;
  private readonly ITypedConverter<TeklaObject, TSM.RadialGrid> _radialGridConverter;
  private readonly ITypedConverter<Base, TSM.ModelObject> _builtElementConverter;
  private readonly ITypedConverter<RevitObject, TSM.ContourPlate> _revitContourPlateConverter;
  private readonly ITypedConverter<RevitObject, TSM.Part> _revitBeamConverter;
  private readonly RevitWallToTeklaBeamConverter _revitWallBeamConverter;
  private readonly RevitFoundationToTeklaConverter _revitFoundationConverter;
  private readonly RevitOpeningToBooleanPartConverter _revitOpeningConverter;
  private readonly GeometricItemToHostConverter _genericConverter;
  private readonly SubComponentToHostConverter _subComponentConverter;
  private readonly TeklaReceiveCache _receiveCache;
  private readonly ITypedConverter<DataObject, TSM.ContourPlate> _ifcFloorConverter;
  private readonly ITypedConverter<DataObject, TSM.Part> _ifcColumnBeamConverter;
  private readonly IfcWallToTeklaBeamConverter _ifcWallBeamConverter;
  private readonly IfcFoundationToTeklaConverter _ifcFoundationConverter;
  private readonly IfcOpeningToBooleanPartConverter _ifcOpeningConverter;

  public TeklaRootToHostConverter(
    IConverterSettingsStore<TeklaConversionSettings> settingsStore,
    ITypedConverter<TeklaObject, TSM.Beam> beamConverter,
    ITypedConverter<TeklaObject, TSM.ContourPlate> contourPlateConverter,
    ITypedConverter<TeklaObject, TSM.PolyBeam> polyBeamConverter,
    ITypedConverter<TeklaObject, TSM.BentPlate> bentPlateConverter,
    ITypedConverter<TeklaObject, TSM.SpiralBeam> spiralBeamConverter,
    ITypedConverter<TeklaObject, TSM.LoftedPlate> loftedPlateConverter,
    ITypedConverter<TeklaObject, TSM.Grid> gridConverter,
    ITypedConverter<TeklaObject, TSM.RadialGrid> radialGridConverter,
    ITypedConverter<Base, TSM.ModelObject> builtElementConverter,
    ITypedConverter<RevitObject, TSM.ContourPlate> revitContourPlateConverter,
    ITypedConverter<RevitObject, TSM.Part> revitBeamConverter,
    RevitWallToTeklaBeamConverter revitWallBeamConverter,
    RevitFoundationToTeklaConverter revitFoundationConverter,
    RevitOpeningToBooleanPartConverter revitOpeningConverter,
    GeometricItemToHostConverter genericConverter,
    SubComponentToHostConverter subComponentConverter,
    TeklaReceiveCache receiveCache,
    ITypedConverter<DataObject, TSM.ContourPlate> ifcFloorConverter,
    ITypedConverter<DataObject, TSM.Part> ifcColumnBeamConverter,
    IfcWallToTeklaBeamConverter ifcWallBeamConverter,
    IfcFoundationToTeklaConverter ifcFoundationConverter,
    IfcOpeningToBooleanPartConverter ifcOpeningConverter
  )
  {
    _settingsStore = settingsStore;
    _beamConverter = beamConverter;
    _contourPlateConverter = contourPlateConverter;
    _polyBeamConverter = polyBeamConverter;
    _bentPlateConverter = bentPlateConverter;
    _spiralBeamConverter = spiralBeamConverter;
    _loftedPlateConverter = loftedPlateConverter;
    _gridConverter = gridConverter;
    _radialGridConverter = radialGridConverter;
    _builtElementConverter = builtElementConverter;
    _revitContourPlateConverter = revitContourPlateConverter;
    _revitBeamConverter = revitBeamConverter;
    _revitWallBeamConverter = revitWallBeamConverter;
    _revitFoundationConverter = revitFoundationConverter;
    _revitOpeningConverter = revitOpeningConverter;
    _genericConverter = genericConverter;
    _subComponentConverter = subComponentConverter;
    _receiveCache = receiveCache;
    _ifcFloorConverter = ifcFloorConverter;
    _ifcColumnBeamConverter = ifcColumnBeamConverter;
    _ifcWallBeamConverter = ifcWallBeamConverter;
    _ifcFoundationConverter = ifcFoundationConverter;
    _ifcOpeningConverter = ifcOpeningConverter;
  }

  public object Convert(Base target)
  {
    if (target is TeklaObject teklaObject)
    {
      TSM.ModelObject? result = teklaObject.type switch
      {
        "Beam" or "Column" => _beamConverter.Convert(teklaObject),
        "ContourPlate" => _contourPlateConverter.Convert(teklaObject),
        "PolyBeam" => _polyBeamConverter.Convert(teklaObject),
        "BentPlate" => _bentPlateConverter.Convert(teklaObject),
        "SpiralBeam" => _spiralBeamConverter.Convert(teklaObject),
        "LoftedPlate" => _loftedPlateConverter.Convert(teklaObject),
        "Grid" => _gridConverter.Convert(teklaObject),
        "RadialGrid" => _radialGridConverter.Convert(teklaObject),
        _ => null,
      };

      if (result != null)
      {
        _receiveCache.Add(target.id, target.applicationId, result);
        return result;
      }

      throw new ConversionException($"Tekla type '{teklaObject.type}' is not supported for receive.");
    }

    if (target is RevitObject revitObject)
    {
      string builtInCategory = revitObject["builtInCategory"] as string ?? "";

      // Openings are sub-components of an already-created host, not top-level parts - they're
      // resolved/converted in Pass 1.5 after the host exists, never re-cached by applicationId.
      if (builtInCategory.IndexOf("Opening", StringComparison.OrdinalIgnoreCase) >= 0)
      {
        return _revitOpeningConverter.ConvertAsBooleanCut(revitObject);
      }

      // Structural Framing System / Beam System sketch container: it has no independently useful
      // geometry of its own - the member beams it lays out are separate OST_StructuralFraming
      // elements, sent and converted through the regular beam path above. Returning the original
      // target (a non-ModelObject) makes the caller log-and-skip it instead of erroring.
      if (builtInCategory == "OST_StructuralFramingSystem")
      {
        return target;
      }

      TSM.ModelObject? result = builtInCategory switch
      {
        "OST_Walls" => _revitWallBeamConverter.Convert(revitObject),
        "OST_Floors" => _revitContourPlateConverter.Convert(revitObject),
        "OST_StructuralColumns" or "OST_StructuralFraming" => _revitBeamConverter.Convert(revitObject),
        "OST_StructuralFoundation" => _revitFoundationConverter.Convert(revitObject),
        _ => null,
      };

      if (result != null)
      {
        _receiveCache.Add(target.id, target.applicationId, result);
        return result;
      }

      throw new ConversionException($"RevitObject category '{builtInCategory}' is not supported for Tekla receive.");
    }

    // Plain DataObject enriched by the IFC native-reconstruction feature (shared with the Revit
    // connector - see RevitNativeSchemaEnricher). Checked after RevitObject/TeklaObject above (both
    // are DataObject subclasses; a genuine instance of either already returned/threw by this point),
    // and gated on builtInCategory being present so an un-enriched, still-DirectShape-bound
    // DataObject falls through to the generic/native path below exactly as before this feature.
    if (target is DataObject dataObject && dataObject["builtInCategory"] is string ifcBuiltInCategory)
    {
      if (ifcBuiltInCategory.IndexOf("Opening", StringComparison.OrdinalIgnoreCase) >= 0)
      {
        return _ifcOpeningConverter.ConvertAsBooleanCut(dataObject);
      }

      // Pure grouping container (e.g. a Revit beam-grid/framing assembly, marked by
      // RevitNativeSchemaEnricher.TryEnrichElementAssembly) with no independent geometry of its own -
      // its member elements are separate entities, already converted individually elsewhere. Mirrors
      // the RevitObject OST_StructuralFramingSystem case above: returning the original target (a
      // non-ModelObject) makes the caller log-and-skip it instead of erroring.
      if (ifcBuiltInCategory == "IfcElementAssembly")
      {
        return target;
      }

      TSM.ModelObject? result = ifcBuiltInCategory switch
      {
        "OST_Walls" => _ifcWallBeamConverter.Convert(dataObject),
        "OST_Floors" => _ifcFloorConverter.Convert(dataObject),
        "OST_StructuralColumns" or "OST_StructuralFraming" => _ifcColumnBeamConverter.Convert(dataObject),
        "OST_StructuralFoundation" => _ifcFoundationConverter.Convert(dataObject),
        _ => null,
      };

      if (result != null)
      {
        _receiveCache.Add(target.id, target.applicationId, result);
        return result;
      }

      throw new ConversionException(
        $"IFC-enriched category '{ifcBuiltInCategory}' is not supported for Tekla receive."
      );
    }

    if (_settingsStore.Current.ReceiveMode == ReceiveMode.Native)
    {
      try
      {
        return _builtElementConverter.Convert(target);
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        return _genericConverter.Convert(target);
      }
    }

    return _genericConverter.Convert(target);
  }
}
