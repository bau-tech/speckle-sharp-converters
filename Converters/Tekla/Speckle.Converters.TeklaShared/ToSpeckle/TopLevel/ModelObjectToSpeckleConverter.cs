using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Converters.TeklaShared.Extensions;
using Speckle.Converters.TeklaShared.Helpers;
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
  private readonly TeklaOutgoingApplicationIdResolver _outgoingApplicationIdResolver;

  public ModelObjectToSpeckleConverter(
    IConverterSettingsStore<TeklaConversionSettings> settingsStore,
    DisplayValueExtractor displayValueExtractor,
    PropertiesExtractor propertiesExtractor,
    ClassPropertyExtractor classPropertyExtractor,
    LocationExtractor locationExtractor,
    TeklaOutgoingApplicationIdResolver outgoingApplicationIdResolver
  )
  {
    _settingsStore = settingsStore;
    _displayValueExtractor = displayValueExtractor;
    _propertiesExtractor = propertiesExtractor;
    _classPropertyExtractor = classPropertyExtractor;
    _locationExtractor = locationExtractor;
    _outgoingApplicationIdResolver = outgoingApplicationIdResolver;
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
      child.applicationId = _outgoingApplicationIdResolver.Resolve(childObject);
      children.Add(child);
    }

    // get display value — suppress display for boolean operative parts (they render via their parent)
    List<Base> rawDisplayValue = _displayValueExtractor.GetDisplayValue(target).ToList();
    IEnumerable<Base> displayValue = rawDisplayValue;
    string? wallOpeningHostApplicationId = null;
    List<SOG.Mesh>? wallOpeningMeshes = null;

    // A wall panel's boolean-cut void reaches here as the TSM.BooleanPart wrapper itself (confirmed
    // live - GetSupportedChildren() yields the BooleanPart, not its OperativePart cutter shape), whose
    // own location/display value are empty (LocationExtractor/DisplayValueExtractor have no case for
    // TSM.BooleanPart) - so the real cutter geometry and host wall have to be read directly off the
    // BooleanPart's own Father/OperativePart properties, not inferred from the traversal's parent arg.
    if (
      target is TSM.BooleanPart bp
      && bp.Type == TSM.BooleanPart.BooleanTypeEnum.BOOLEAN_CUT
      && bp.Father is TSM.Part fatherPart
      && fatherPart.Class == TeklaStandardClasses.CONCRETE_PANEL
      && bp.OperativePart is TSM.Part operativePart
    )
    {
      wallOpeningHostApplicationId = _outgoingApplicationIdResolver.Resolve(fatherPart);
      wallOpeningMeshes = _displayValueExtractor.GetDisplayValue(operativePart).OfType<SOG.Mesh>().ToList();
    }

    // Suppress render for an operative part that shows up as its own sibling child of the father
    // part (rather than only reachable via the BooleanPart wrapper above) - it renders via its
    // parent already, so a second copy would double up in the viewer.
    if (parent is TSM.Part parentPart)
    {
      var booleans = parentPart.GetBooleans();
      var targetGuid = target.Identifier.GUID;
      while (booleans.MoveNext())
      {
        if (booleans.Current is TSM.BooleanPart siblingBp && siblingBp.OperativePart?.Identifier.GUID == targetGuid)
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
    // A wall-opening cutter gets a synthetic rectangular boundary instead of its own native shape,
    // and a "parentApplicationId" tag - the exact convention RevitRootToHostConverter's Tekla dispatch
    // and OpeningToHostConverter.CreateHostedOpening (Revit receive side) already expect for any hosted
    // opening, mirroring how a literal Revit Opening element is captured on the Revit send side.
    Base? location;
    if (wallOpeningHostApplicationId is not null)
    {
      location = WallOpeningBoundaryBuilder.TryBuildRectangularBoundary(
        wallOpeningMeshes ?? [],
        _settingsStore.Current.SpeckleUnits
      );
      properties["parentApplicationId"] = wallOpeningHostApplicationId;
    }
    else
    {
      location = _locationExtractor.GetLocation(target);
    }

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
      applicationId = _outgoingApplicationIdResolver.Resolve(target)
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
