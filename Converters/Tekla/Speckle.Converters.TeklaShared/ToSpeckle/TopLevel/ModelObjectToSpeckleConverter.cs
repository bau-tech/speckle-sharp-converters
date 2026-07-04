using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Converters.TeklaShared.Extensions;
using Speckle.Converters.TeklaShared.ToSpeckle.Helpers;
using Speckle.Objects.Data;
using Speckle.Sdk.Models;

namespace Speckle.Converters.TeklaShared.ToSpeckle.TopLevel;

[NameAndRankValue(typeof(TSM.ModelObject), NameAndRankValueAttribute.SPECKLE_DEFAULT_RANK)]
public class ModelObjectToSpeckleConverter : IToSpeckleTopLevelConverter
{
  private readonly IConverterSettingsStore<TeklaConversionSettings> _settingsStore;
  private readonly DisplayValueExtractor _displayValueExtractor;
  private readonly PropertiesExtractor _propertiesExtractor;
  private readonly ClassPropertyExtractor _classPropertyExtractor;
  private readonly LocationExtractor _locationExtractor;

  public ModelObjectToSpeckleConverter(
    IConverterSettingsStore<TeklaConversionSettings> settingsStore,
    DisplayValueExtractor displayValueExtractor,
    PropertiesExtractor propertiesExtractor,
    ClassPropertyExtractor classPropertyExtractor,
    LocationExtractor locationExtractor
  )
  {
    _settingsStore = settingsStore;
    _displayValueExtractor = displayValueExtractor;
    _propertiesExtractor = propertiesExtractor;
    _classPropertyExtractor = classPropertyExtractor;
    _locationExtractor = locationExtractor;
  }

  public Base Convert(object target) => Convert((TSM.ModelObject)target, null);

  private TeklaObject Convert(TSM.ModelObject target, TSM.ModelObject? parent)
  {
    string type = target.GetType().ToString().Split('.').Last();

    // get children (same logic as material unpacker in connector)
    List<TeklaObject> children = new();
    foreach (TSM.ModelObject childObject in target.GetSupportedChildren())
    {
      var child = Convert(childObject, target);
      child.applicationId = childObject.GetSpeckleApplicationId();
      children.Add(child);
    }

    // get display value — suppress display for boolean operative parts (they render via their parent)
    IEnumerable<Base> displayValue = _displayValueExtractor.GetDisplayValue(target).ToList();
    if (parent is TSM.Part parentPart)
    {
      var booleans = parentPart.GetBooleans();
      var targetGuid = target.Identifier.GUID;
      while (booleans.MoveNext())
      {
        if (
          booleans.Current is TSM.BooleanPart bp
          && bp.OperativePart?.Identifier.GUID == targetGuid
        )
        {
          displayValue = [];
          break;
        }
      }
    }

    // get name
    string name = type;
    switch (target)
    {
      case TSM.Part part:
        name = part.Name;
        break;
      case TSM.Reinforcement reinforcement:
        name = reinforcement.Name;
        break;
    }

    // get properties
    var properties = _propertiesExtractor.GetProperties(target);

    // get location (stored as dynamic property so receivers can access it via target["location"])
    var location = _locationExtractor.GetLocation(target);

    // Use Speckle.Objects.Data.TeklaObject — its lowercase property names match Speckle viewer conventions.
    // Our custom Speckle.Converters.TeklaShared.TeklaObject used uppercase which the viewer couldn't read.
    var result = new TeklaObject()
    {
      name = name,
      type = type,
      elements = children,
      properties = properties,
      displayValue = displayValue.ToList(),
      units = _settingsStore.Current.SpeckleUnits,
      applicationId = target.GetSpeckleApplicationId()
    };

    // Store location as a dynamic property for the receive-side converters.
    if (location is not null)
    {
      result["location"] = location;
    }

    // Also expose each property at the top level so the Speckle viewer shows them in the properties panel.
    foreach (var kvp in properties)
    {
      result[kvp.Key] = kvp.Value;
    }

    return result;
  }
}
