using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Converters.Common.ToSpeckle;
using Speckle.Converters.RevitShared.Extensions;
using Speckle.Converters.RevitShared.Helpers;
using Speckle.Converters.RevitShared.Settings;
using Speckle.Converters.RevitShared.ToSpeckle.Properties;
using Speckle.DoubleNumerics;
using Speckle.Objects;
using Speckle.Objects.Data;
using Speckle.Sdk;
using Speckle.Sdk.Common;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;
using Speckle.Sdk.Models.Instances;

namespace Speckle.Converters.RevitShared.ToSpeckle;

[NameAndRankValue(typeof(DB.Element), 0)]
public class ElementTopLevelConverterToSpeckle : IToSpeckleTopLevelConverter
{
  private readonly DisplayValueExtractor _displayValueExtractor;
  private readonly PropertiesExtractor _propertiesExtractor;
  private readonly ITypedConverter<DB.Location, Base> _locationConverter;
  private readonly ITypedConverter<DB.Curve, ICurve> _curveConverter;
  private readonly ITypedConverter<DB.CurveArray, SOG.Polycurve> _curveArrayConverter;
  private readonly LevelExtractor _levelExtractor;
  private readonly IConverterSettingsStore<RevitConversionSettings> _converterSettings;
  private readonly RevitToSpeckleCacheSingleton _revitToSpeckleCacheSingleton;
  private readonly RevitOutgoingApplicationIdResolver _outgoingApplicationIdResolver;

  public ElementTopLevelConverterToSpeckle(
    DisplayValueExtractor displayValueExtractor,
    RevitToSpeckleCacheSingleton revitToSpeckleCacheSingleton,
    PropertiesExtractor propertiesExtractor,
    LevelExtractor levelExtractor,
    ITypedConverter<DB.Location, Base> locationConverter,
    ITypedConverter<DB.Curve, ICurve> curveConverter,
    ITypedConverter<DB.CurveArray, SOG.Polycurve> curveArrayConverter,
    IConverterSettingsStore<RevitConversionSettings> converterSettings,
    RevitOutgoingApplicationIdResolver outgoingApplicationIdResolver
  )
  {
    _displayValueExtractor = displayValueExtractor;
    _revitToSpeckleCacheSingleton = revitToSpeckleCacheSingleton;
    _propertiesExtractor = propertiesExtractor;
    _levelExtractor = levelExtractor;
    _locationConverter = locationConverter;
    _curveConverter = curveConverter;
    _curveArrayConverter = curveArrayConverter;
    _converterSettings = converterSettings;
    _outgoingApplicationIdResolver = outgoingApplicationIdResolver;
  }

  public Base Convert(object target) => Convert((DB.Element)target);

  private RevitObject Convert(DB.Element target)
  {
    string category = target.Category?.Name ?? "none";

    // locale-independent category identifier (e.g. "OST_Walls"), used by the receiving connector
    // to dispatch native reconstruction without relying on the (localized) display name above.
    string builtInCategory = (target.Category?.GetBuiltInCategory() ?? DB.BuiltInCategory.INVALID).ToString();

    // special case for direct shapes: use builtin category instead
    if (target is DB.DirectShape ds)
    {
      // Clean up built-in name by removing "OST" prefixes
      category = ds
        .Category.GetBuiltInCategory()
        .ToString()
        .Replace("OST_IOS", "") //for OST_IOSModelGroups
        .Replace("OST_MEP", "") //for OST_MEPSpaces
        .Replace("OST_", "") //for any other OST_blablabla
        .Replace("_", " ");
    }

    string name = $"{category} - {target.Name}"; // Note: I find this looks better in the frontend.
    string familyName = "none";
    string typeName = "none";
    switch (target.Document.GetElement(target.GetTypeId()))
    {
      case DB.FamilySymbol symbol:
        familyName = symbol.FamilyName;
        typeName = symbol.Name;
        break;
      case DB.ElementType type:
        familyName = type.FamilyName;
        typeName = type.Name;
        break;
    }

    // get location if any
    Base? convertedLocation = null;
    List<RevitObject> floorOpenings = [];
    switch (target)
    {
      // skip these objects, if location is redundant
      case DB.ModelCurve:
        break;

      // Grid geometry is exposed via Curve, not via a LocationCurve
      case DB.Grid grid:
        try
        {
          convertedLocation = _curveConverter.Convert(grid.Curve) as Base;
        }
        catch (ValidationException)
        {
          // unsupported curve type (e.g. multi-segment grid) - location stays null
        }
        break;

      // Floors have no LocationCurve/LocationPoint - capture the outer boundary of the top face instead.
      // Any additional (inner) loops on that face are sketch holes - turned into synthetic floor-opening
      // child objects so they can be reconstructed as DB.Opening elements on receive.
      case DB.Floor floor:
        try
        {
          (convertedLocation, floorOpenings) = ExtractFloorBoundaryAndOpenings(floor);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
          // non-planar/unsupported floor geometry - location stays null
        }
        break;

      // Roofs have no LocationCurve/LocationPoint - capture the outer boundary of the bottom face instead
      case DB.RoofBase roof:
        try
        {
          convertedLocation = ExtractRoofBoundary(roof);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
          // non-planar/unsupported roof geometry - location stays null
        }
        break;

      // Openings have no LocationCurve/LocationPoint - capture their boundary instead
      case DB.Opening opening:
        try
        {
          convertedLocation = ExtractOpeningBoundary(opening);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
          // unsupported opening geometry - location stays null
        }
        break;

      default:
        if (target.Location is DB.Location location and (DB.LocationCurve or DB.LocationPoint)) // location can be null
        {
          try
          {
            convertedLocation = _locationConverter.Convert(location);
          }
          catch (ValidationException)
          {
            // NOTE: i've improved the if check above to make sure we never reach here
            // we were throwing a lot here for various elements (e.g. floors) and we would
            // be slowing things down
            // location was not a supported, do not attach to base element
          }
        }
        break;
    }

    // get the display value
    List<DisplayValueResult> displayValuesWithTransforms = _displayValueExtractor.GetDisplayValue(target);

    // process display values and create instance proxies where applicable
    List<Base> proxifiedDisplayValues = ProcessDisplayValues(target.Id.ToString(), displayValuesWithTransforms);

    // get level
    string? level = _levelExtractor.GetLevelName(target);

    // get children elements
    // this is a bespoke method by class type.
    var children = GetElementChildren(target).Concat(floorOpenings).ToList();

    // get properties
    Dictionary<string, object?> properties = _propertiesExtractor.GetProperties(target);

    RevitObject revitObject = new()
    {
      name = name,
      type = typeName,
      family = familyName,
      level = level,
      category = category,
      location = convertedLocation,
      elements = children,
      displayValue = proxifiedDisplayValues,
      properties = properties,
      units = _converterSettings.Current.SpeckleUnits,
    };

    revitObject["builtInCategory"] = builtInCategory;

    return revitObject;
  }

  /// <summary>
  /// Extracts the outer boundary of a floor's top face as a closed polycurve, for use as the floor's location,
  /// along with synthetic floor-opening child objects for any inner (hole) loops on that face.
  /// Returns a null boundary if the floor has no top face or the face has no edges (e.g. unsupported geometry).
  /// </summary>
  private (SOG.Polycurve? boundary, List<RevitObject> openings) ExtractFloorBoundaryAndOpenings(DB.Floor floor)
  {
    // A floor's top face can be split across multiple references (e.g. shape-edited sub-regions),
    // and not every reference necessarily resolves to a face with edges - try each until one works.
    foreach (DB.Reference faceRef in DB.HostObjectUtils.GetTopFaces(floor))
    {
      if (floor.GetGeometryObjectFromReference(faceRef) is not DB.Face face)
      {
        continue;
      }

      IList<DB.CurveLoop> loops = face.GetEdgesAsCurveLoops();
      if (loops.Count == 0)
      {
        continue;
      }

      DB.CurveLoop outerLoop = loops.OrderByDescending(l => l.GetExactLength()).First();
      SOG.Polycurve? boundary = ConvertCurveLoopToPolycurve(outerLoop);

      List<RevitObject> openings = [];
      foreach (DB.CurveLoop holeLoop in loops.Where(l => l != outerLoop))
      {
        try
        {
          openings.Add(CreateFloorOpening(floor, holeLoop));
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
          // unsupported hole geometry - skip this opening
        }
      }

      return (boundary, openings);
    }

    return (null, []);
  }

  private SOG.Polycurve? ConvertCurveLoopToPolycurve(DB.CurveLoop loop)
  {
    DB.CurveArray curveArray = new();
    foreach (DB.Curve curve in loop)
    {
      curveArray.Append(curve);
    }

    return _curveArrayConverter.Convert(curveArray);
  }

  /// <summary>
  /// Builds a synthetic floor-opening object for a hole loop in a floor's sketch, so it can be reconstructed
  /// as a <see cref="DB.Opening"/> hosted on the floor on receive.
  /// </summary>
  private RevitObject CreateFloorOpening(DB.Floor floor, DB.CurveLoop holeLoop)
  {
    RevitObject opening = new()
    {
      name = "Floor Opening",
      type = "Floor Opening",
      family = "none",
      level = null,
      category = "Floor Openings",
      location = ConvertCurveLoopToPolycurve(holeLoop),
      elements = [],
      displayValue = [],
      properties = new Dictionary<string, object?>
      {
        ["parentApplicationId"] = _outgoingApplicationIdResolver.Resolve(floor),
      },
      units = _converterSettings.Current.SpeckleUnits,
    };

    opening["builtInCategory"] = DB.BuiltInCategory.OST_FloorOpening.ToString();

    return opening;
  }

  /// <summary>
  /// Extracts the outer boundary of a roof's bottom face as a closed polycurve, for use as the roof's location.
  /// Returns null if the roof has no bottom face or the face has no edges (e.g. unsupported geometry).
  /// </summary>
  private SOG.Polycurve? ExtractRoofBoundary(DB.RoofBase roof)
  {
    IList<DB.Reference> bottomFaces = DB.HostObjectUtils.GetBottomFaces(roof);
    if (bottomFaces.Count == 0)
    {
      return null;
    }

    if (roof.GetGeometryObjectFromReference(bottomFaces[0]) is not DB.Face face)
    {
      return null;
    }

    IList<DB.CurveLoop> loops = face.GetEdgesAsCurveLoops();
    if (loops.Count == 0)
    {
      return null;
    }

    DB.CurveLoop outerLoop = loops.OrderByDescending(l => l.GetExactLength()).First();

    DB.CurveArray curveArray = new();
    foreach (DB.Curve curve in outerLoop)
    {
      curveArray.Append(curve);
    }

    return _curveArrayConverter.Convert(curveArray);
  }

  /// <summary>
  /// Extracts an opening's boundary as a closed polycurve, for use as the opening's location.
  /// Rectangular openings (<see cref="DB.Opening.IsRectBoundary"/>) expose only two diagonal corner
  /// points via <see cref="DB.Opening.BoundaryRect"/>; the other two corners are derived assuming the
  /// rectangle has one pair of vertical edges (constant X/Y) and one pair of horizontal edges (constant Z),
  /// which holds for openings cut perpendicular to a wall. Returns null if no boundary is available.
  /// </summary>
  private SOG.Polycurve? ExtractOpeningBoundary(DB.Opening opening)
  {
    DB.CurveArray curveArray = new();

    if (opening.IsRectBoundary)
    {
      IList<DB.XYZ> rect = opening.BoundaryRect;
      if (rect.Count < 2)
      {
        return null;
      }

      DB.XYZ p0 = rect[0];
      DB.XYZ p1 = rect[1];
      DB.XYZ p2 = new(p0.X, p0.Y, p1.Z);
      DB.XYZ p3 = new(p1.X, p1.Y, p0.Z);

      curveArray.Append(DB.Line.CreateBound(p0, p2));
      curveArray.Append(DB.Line.CreateBound(p2, p1));
      curveArray.Append(DB.Line.CreateBound(p1, p3));
      curveArray.Append(DB.Line.CreateBound(p3, p0));
    }
    else
    {
      DB.CurveArray boundaryCurves = opening.BoundaryCurves;
      if (boundaryCurves.Size == 0)
      {
        return null;
      }

      foreach (DB.Curve curve in boundaryCurves)
      {
        curveArray.Append(curve);
      }
    }

    return _curveArrayConverter.Convert(curveArray);
  }

  private IEnumerable<RevitObject> GetElementChildren(DB.Element element)
  {
    var childrenIds = element.GetKnownChildrenElements().ToList();
    foreach (var childrenId in childrenIds)
    {
      var childElement = _converterSettings.Current.Document.GetElement(childrenId);
      yield return ConvertChildAndSetOpeningParent(childElement, element);
    }

    // GetKnownChildrenElements does not surface DB.Opening elements hosted on Walls/Columns/Beams
    // (it only covers curtain grid mullions/panels, stacked wall members, footprint roof curtain
    // grids, and railing top rails) - so hosted openings need a supplemental lookup here.
    foreach (DB.Opening opening in GetOpeningsByHostId().GetOrDefault(element.Id, []))
    {
      if (childrenIds.Contains(opening.Id))
      {
        continue;
      }

      yield return ConvertChildAndSetOpeningParent(opening, element);
    }
  }

  private RevitObject ConvertChildAndSetOpeningParent(DB.Element childElement, DB.Element parent)
  {
    var child = Convert(childElement);

    if (
      child.category.ContainsOrdinalIgnoreCase("Opening")
      && child.properties.GetOrDefault("parentApplicationId") is not string
    )
    {
      // Must match whatever applicationId the parent ITSELF gets emitted with (see
      // RevitOutgoingApplicationIdResolver) - a parent round-tripped from another app (e.g. a wall
      // originally authored in Tekla) is sent under its cross-app origin id, not its own fresh Revit
      // UniqueId, so using UniqueId here would leave the opening pointing at a host key nothing is
      // ever cached under on receive.
      child.properties["parentApplicationId"] = _outgoingApplicationIdResolver.Resolve(parent);
    }

    return child;
  }

  /// <summary>
  /// Lazily builds a lookup of all <see cref="DB.Opening"/> elements in the document grouped by
  /// their host's <see cref="DB.ElementId"/>, avoiding a per-element <see cref="DB.FilteredElementCollector"/>
  /// query inside <see cref="GetElementChildren"/> (which would be O(N*M) over a model with many
  /// elements and openings).
  /// </summary>
  private Dictionary<DB.ElementId, List<DB.Opening>> GetOpeningsByHostId()
  {
    if (_openingsByHostId is not null)
    {
      return _openingsByHostId;
    }

    using DB.FilteredElementCollector collector = new(_converterSettings.Current.Document);
    _openingsByHostId = collector
      .OfClass(typeof(DB.Opening))
      .Cast<DB.Opening>()
      .Where(o => o.Host is not null)
      .GroupBy(o => o.Host.Id)
      .ToDictionary(g => g.Key, g => g.ToList());

    return _openingsByHostId;
  }

  private Dictionary<DB.ElementId, List<DB.Opening>>? _openingsByHostId;

  /// <summary>
  /// Processes display values with transforms and creates instance proxies for meshes that can be instanced.
  /// Also populates material proxy objects lists with the appropriate mesh IDs based on whether geometry is instanced or not.
  /// </summary>
  /// <returns>
  /// List of processed display values, with meshes replaced by instance proxies where applicable.
  /// Non-instance geometry is returned as-is.
  /// </returns>
  /// <remarks>
  /// <para>
  /// This is a bit of a code smell. This method is doing too much, "this ... AND this...".
  /// </para>
  /// <para>
  /// But, given a mesh:
  /// - if it has a transform, mesh is converted to instance proxy, and the definition mesh ID is added to material proxies
  /// - if it doesn't have a transform, it remains as a regular mesh, and its own ID is added to material proxies
  /// - other geometry types pass through unchanged
  /// </para>
  /// <para>
  /// This is where material proxy population occurs (deferred from <see cref="MeshByMaterialDictionaryToSpeckle.Convert"/>)
  /// to ensure we use definition mesh IDs for instances rather than individual instance mesh IDs.
  /// </para>
  /// </remarks>
  private List<Base> ProcessDisplayValues(string elementId, List<DisplayValueResult> displayValues)
  {
    List<Base> proxifiedDisplayValues = new();

    foreach (var displayValue in displayValues)
    {
      // check if this is a mesh with a transform - potential instance scenario
      if (displayValue.Geometry is SOG.Mesh mesh && displayValue.Transform is not null)
      {
        var instanceProxy = CreateOrGetInstanceProxy(elementId, mesh, displayValue.Transform.Value);
        proxifiedDisplayValues.Add(instanceProxy);

        // add the definition mesh ID to material proxy, not the instance mesh
        // method technically is a "Try" but logs internally, so we don't have a return to check
        _revitToSpeckleCacheSingleton.AddMeshToMaterialProxy(elementId, mesh, isInstance: true);
      }
      else if (displayValue.Geometry is SOG.Mesh nonInstanceMesh)
      {
        // non-instance mesh - add its own ID to material proxy
        // method technically is a "Try" but logs internally, so we don't have a return to check
        _revitToSpeckleCacheSingleton.AddMeshToMaterialProxy(elementId, nonInstanceMesh, isInstance: false);
        proxifiedDisplayValues.Add(nonInstanceMesh);
      }
      else
      {
        proxifiedDisplayValues.Add(displayValue.Geometry);
      }
    }

    return proxifiedDisplayValues;
  }

  /// <summary>
  /// Creates or retrieves an instance proxy for a mesh, managing instance definitions and caching.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This method generates a deterministic instance definition ID based on the untransformed mesh geometry using
  /// <see cref="MeshInstanceIdGenerator.GenerateUntransformedMeshId"/>. Multiple instances with identical geometry
  /// will share the same definition.
  /// </para>
  /// <para>
  /// The method manages two caches:
  /// - <see cref="RevitToSpeckleCacheSingleton.InstanceDefinitionProxiesMap"/>: Tracks instance definitions and which elements use them
  /// - <see cref="RevitToSpeckleCacheSingleton.InstancedObjects"/>: Stores the actual definition meshes for later serialization
  /// </para>
  /// </remarks>
  private InstanceProxy CreateOrGetInstanceProxy(string elementId, SOG.Mesh mesh, Matrix4x4 transform)
  {
    var instanceDefinitionId = MeshInstanceIdGenerator.GenerateUntransformedMeshId(mesh);
    var materialId = _revitToSpeckleCacheSingleton.GetMaterialId(elementId, mesh);
    instanceDefinitionId += materialId;

    // We need to attach element id relationship to proxy singleton for send caching.
    // Send caching skips whole DB.Element that turn into RevitDataObject. since we have instance proxies in RevitDataObject but
    // its definitions outside of caching mechanism, this elementId helps us to filter which definition proxies should be attached to the root
    if (
      _revitToSpeckleCacheSingleton.InstanceDefinitionProxiesMap.TryGetValue(
        instanceDefinitionId,
        out var instanceDefinition
      )
    )
    {
      instanceDefinition.elementIds.Add(elementId);
    }
    else
    {
      var newInstanceDefinition = new InstanceDefinitionProxy
      {
        applicationId = instanceDefinitionId,
        objects = new List<string> { mesh.applicationId.NotNull() },
        maxDepth = 0,
        name = instanceDefinitionId,
      };
      _revitToSpeckleCacheSingleton.InstanceDefinitionProxiesMap.Add(
        instanceDefinitionId,
        ([elementId], newInstanceDefinition)
      );
    }

    // some comment valid here as above if statement, since we store original meshes outside of RevitDataObject, we need to know which of them will be attached.
    if (_revitToSpeckleCacheSingleton.InstancedObjects.TryGetValue(instanceDefinitionId, out var instancedObject))
    {
      instancedObject.elementIds.Add(elementId);
    }
    else
    {
      _revitToSpeckleCacheSingleton.InstancedObjects.Add(instanceDefinitionId, ([elementId], mesh));
    }

    // create and return instance proxy with transform
    var instanceProxy = new InstanceProxy
    {
      applicationId = Guid.NewGuid().ToString(),
      definitionId = instanceDefinitionId,
      transform = transform,
      maxDepth = 0,
      units = mesh.units,
    };

    return instanceProxy;
  }
}
