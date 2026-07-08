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
    string? hostedOpeningHostApplicationId = null;
    List<SOG.Mesh>? hostedOpeningMeshes = null;
    TSM.Part? hostedOpeningOperativePart = null;

    // A wall or slab panel's boolean-cut void reaches here as the TSM.BooleanPart wrapper itself
    // (confirmed live - GetSupportedChildren() yields the BooleanPart, not its OperativePart cutter
    // shape), whose own location/display value are empty (LocationExtractor/DisplayValueExtractor
    // have no case for TSM.BooleanPart) - so the real cutter geometry and host part have to be read
    // directly off the BooleanPart's own Father/OperativePart properties, not inferred from the
    // traversal's parent arg. Covers both CONCRETE_PANEL (wall) and CONCRETE_SLAB (floor) hosts -
    // WallOpeningBoundaryBuilder's bbox-axis-drop is orientation-agnostic, and OpeningToHostConverter's
    // receive-side fallback (doc.Create.NewOpening(host, curveArray, true)) already handles any
    // non-Wall host generically - a class==CONCRETE_PANEL-only gate here previously left slab cutouts
    // untagged (no "parentApplicationId"), so they never reached the receive-side opening dispatch at
    // all and silently vanished.
    if (
      target is TSM.BooleanPart bp
      && bp.Type == TSM.BooleanPart.BooleanTypeEnum.BOOLEAN_CUT
      && bp.Father is TSM.Part fatherPart
      && fatherPart.Class is TeklaStandardClasses.CONCRETE_PANEL or TeklaStandardClasses.CONCRETE_SLAB
      && bp.OperativePart is TSM.Part operativePart
    )
    {
      hostedOpeningHostApplicationId = _outgoingApplicationIdResolver.Resolve(fatherPart);
      hostedOpeningMeshes = _displayValueExtractor.GetDisplayValue(operativePart).OfType<SOG.Mesh>().ToList();
      hostedOpeningOperativePart = operativePart;
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
    // A hosted-opening cutter gets a "parentApplicationId" tag - the exact convention
    // RevitRootToHostConverter's Tekla dispatch and OpeningToHostConverter.CreateHostedOpening (Revit
    // receive side) already expect for any hosted opening, mirroring how a literal Revit Opening
    // element is captured on the Revit send side. Prefer the cutter's own native contour (same
    // LocationExtractor.GetLocation path a TSM.ContourPlate's outer contour uses, chamfers included -
    // slab cutouts are commonly authored as a small chamfered ContourPlate cutter) over the synthetic
    // rectangular boundary, since OpeningToHostConverter only flattens to a bounding box for a Wall
    // host - a floor host gets the real curve array, so a rectangle there would silently square off
    // rounded/chamfered cutout corners. Falls back to the rectangle for any cutter shape with no
    // contour points (e.g. a plain box cutter for a wall opening, where the receive side flattens to
    // a bbox anyway).
    Base? location;
    if (hostedOpeningHostApplicationId is not null)
    {
      location =
        (hostedOpeningOperativePart is not null ? _locationExtractor.GetLocation(hostedOpeningOperativePart) : null)
        ?? WallOpeningBoundaryBuilder.TryBuildRectangularBoundary(
          hostedOpeningMeshes ?? [],
          _settingsStore.Current.SpeckleUnits
        );
      properties["parentApplicationId"] = hostedOpeningHostApplicationId;
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
      applicationId = _outgoingApplicationIdResolver.Resolve(target),
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
