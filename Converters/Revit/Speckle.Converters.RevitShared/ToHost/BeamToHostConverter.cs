using Autodesk.Revit.DB;
using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Converters.RevitShared.Settings;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.RevitShared.ToHost;

public class BeamToHostConverter : ITypedConverter<Base, DB.Element>
{
  private readonly IConverterSettingsStore<RevitConversionSettings> _settingsStore;
  private readonly ITypedConverter<SOG.Line, DB.Line> _lineConverter;

  public BeamToHostConverter(
    IConverterSettingsStore<RevitConversionSettings> settingsStore,
    ITypedConverter<SOG.Line, DB.Line> lineConverter
  )
  {
    _settingsStore = settingsStore;
    _lineConverter = lineConverter;
  }

  public DB.Element Convert(Base target)
  {
    var doc = _settingsStore.Current.Document;

    // 1. Get Location
    if (target["location"] is not SOG.Line speckleLine)
    {
      throw new ConversionException("Native Beam requires a line location.");
    }

    DB.Line revitLine = _lineConverter.Convert(speckleLine);

    // 2. Find Family Symbol (Simplified for now)
    // In a real scenario, we would map the 'type' or 'profile' property to a Revit FamilySymbol
    string typeName = target["type"] as string ?? "HEA 200"; // Default or mapped

    FamilySymbol? symbol;
    using (var collector = new FilteredElementCollector(doc))
    {
      symbol = collector
        .OfClass(typeof(FamilySymbol))
        .OfCategory(BuiltInCategory.OST_StructuralFraming)
        .Cast<FamilySymbol>()
        .FirstOrDefault(s => s.Name == typeName);
    }

    if (symbol == null)
    {
      // Fallback to first available if named one not found
      using (var collector = new FilteredElementCollector(doc))
      {
        symbol = collector
          .OfClass(typeof(FamilySymbol))
          .OfCategory(BuiltInCategory.OST_StructuralFraming)
          .Cast<FamilySymbol>()
          .FirstOrDefault();
      }
    }

    if (symbol == null)
    {
      throw new ConversionException("No Structural Framing family symbols found in the document.");
    }

    if (!symbol.IsActive)
    {
      symbol.Activate();
    }

    // 3. Find Level (Simplified)
    Level? level;
    using (var collector = new FilteredElementCollector(doc))
    {
      level = collector.OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();
    }

    if (level == null)
    {
      throw new ConversionException("No levels found in the document.");
    }

    // 4. Create Native Beam
    FamilyInstance beam = doc.Create.NewFamilyInstance(revitLine, symbol, level, DB.Structure.StructuralType.Beam);

    return beam;
  }

  public object Convert(object target) => Convert((Base)target);
}
