using Autodesk.Revit.DB;
using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Converters.RevitShared.Settings;
using Speckle.Sdk.Common;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.RevitShared;

public record DirectShapeDefinitionWrapper(string DefinitionId, List<GeometryObject> Geometries);

public class RevitRootToHostConverter : IRootToHostConverter
{
  private readonly IConverterSettingsStore<RevitConversionSettings> _converterSettings;
  private readonly ITypedConverter<Base, List<DB.GeometryObject>> _baseToGeometryConverter;
  private readonly ITypedConverter<Base, DB.Element> _beamConverter;

  public RevitRootToHostConverter(
    ITypedConverter<Base, List<DB.GeometryObject>> baseToGeometryConverter,
    IConverterSettingsStore<RevitConversionSettings> converterSettings,
    ITypedConverter<Base, DB.Element> beamConverter
  )
  {
    _baseToGeometryConverter = baseToGeometryConverter;
    _converterSettings = converterSettings;
    _beamConverter = beamConverter;
  }

  public object Convert(Base target)
  {
    // Check for Native mode
    if (_converterSettings.Current.ReceiveMode == Speckle.Converters.RevitShared.Settings.ReceiveMode.Native)
    {
      // Try native conversion for supported types
      // For now, let's check for 'Beam' or 'Structural Framing' types
      string type = target["type"] as string ?? "";
      if (type.Contains("Beam") || type.Contains("Column"))
      {
#pragma warning disable CA1031 // Do not catch general exception types
        try
        {
          return _beamConverter.Convert(target);
        }
        catch (Exception)
        {
          // Fallback to DirectShape if native fails or type not fully supported
          // Or just log and continue if that's the policy
        }
#pragma warning restore CA1031 // Do not catch general exception types
      }
    }

    // Use default behavior and covert everything to DirectShapes
    List<DB.GeometryObject> geometryObjects = _baseToGeometryConverter.Convert(target);

    if (geometryObjects.Count == 0)
    {
      throw new ConversionException($"No supported conversion for {target.speckle_type} found.");
    }

    var definitionId = target.applicationId ?? target.id.NotNull();
    DirectShapeLibrary
      .GetDirectShapeLibrary(_converterSettings.Current.Document)
      .AddDefinition(definitionId, geometryObjects);

    return new DirectShapeDefinitionWrapper(definitionId, geometryObjects);
  }
}
