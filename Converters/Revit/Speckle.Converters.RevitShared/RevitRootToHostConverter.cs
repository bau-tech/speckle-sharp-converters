using Autodesk.Revit.DB;
using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Converters.RevitShared.Helpers;
using Speckle.Converters.RevitShared.Settings;
using Speckle.Objects.Data;
using Speckle.Sdk;
using Speckle.Sdk.Common;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;
using RevitReceiveMode = Speckle.Converters.RevitShared.Settings.ReceiveMode;

namespace Speckle.Converters.RevitShared;

public record DirectShapeDefinitionWrapper(string DefinitionId, List<GeometryObject> Geometries);

// A whole Tekla grid system fans out into N native DB.Grid elements (see
// TeklaGridSystemToHostConverter) - not a single Element, needs special handling in BakeObjects,
// mirroring how DirectShapeDefinitionWrapper is already handled there.
public record GridSystemWrapper(IReadOnlyList<DB.Element> Grids);

public class RevitRootToHostConverter : IRootToHostConverter
{
  private readonly IConverterSettingsStore<RevitConversionSettings> _converterSettings;
  private readonly ITypedConverter<Base, List<DB.GeometryObject>> _baseToGeometryConverter;
  private readonly ITypedConverter<Base, DB.Element> _beamConverter;
  private readonly ToHost.GridToHostConverter _gridConverter;
  private readonly ToHost.TeklaGridSystemToHostConverter _teklaGridSystemConverter;
  private readonly ToHost.ColumnToHostConverter _columnConverter;
  private readonly ToHost.WallToHostConverter _wallConverter;
  private readonly ToHost.FloorToHostConverter _floorConverter;
  private readonly ToHost.OpeningToHostConverter _openingConverter;
  private readonly ToHost.FoundationToHostConverter _foundationConverter;
  private readonly ToHost.RoofToHostConverter _roofConverter;
  private readonly ILogger<RevitRootToHostConverter> _logger;

  public RevitRootToHostConverter(
    ITypedConverter<Base, List<DB.GeometryObject>> baseToGeometryConverter,
    IConverterSettingsStore<RevitConversionSettings> converterSettings,
    ITypedConverter<Base, DB.Element> beamConverter,
    ToHost.GridToHostConverter gridConverter,
    ToHost.TeklaGridSystemToHostConverter teklaGridSystemConverter,
    ToHost.ColumnToHostConverter columnConverter,
    ToHost.WallToHostConverter wallConverter,
    ToHost.FloorToHostConverter floorConverter,
    ToHost.OpeningToHostConverter openingConverter,
    ToHost.FoundationToHostConverter foundationConverter,
    ToHost.RoofToHostConverter roofConverter,
    ILogger<RevitRootToHostConverter> logger
  )
  {
    _baseToGeometryConverter = baseToGeometryConverter;
    _converterSettings = converterSettings;
    _beamConverter = beamConverter;
    _gridConverter = gridConverter;
    _teklaGridSystemConverter = teklaGridSystemConverter;
    _columnConverter = columnConverter;
    _wallConverter = wallConverter;
    _floorConverter = floorConverter;
    _openingConverter = openingConverter;
    _foundationConverter = foundationConverter;
    _roofConverter = roofConverter;
    _logger = logger;
  }

#pragma warning disable CA2000 // returned elements are live document elements owned by Revit, not local disposables
  public object Convert(Base target)
  {
    var mode = _converterSettings.Current.ReceiveMode;

    // NativeRevit: objects from a Revit model are received back as their native Revit element types
    // where a category-specific converter exists. Anything not handled here falls through to
    // DirectShape (or the family-baking strategy, which runs before this converter is reached).
    if (mode == RevitReceiveMode.NativeRevit && TryNativeRevitConvert(target) is { } nativeRevitElement)
    {
      return nativeRevitElement;
    }

    // NativeTekla: objects from Tekla Structures are received as native Revit structural elements.
    // We detect structural framing by speckle_type and by the Tekla category property.
    // NativeRevit: families are handled separately by BakeInstancesAsFamilies(); objects that
    // reach this converter are non-family ones and fall through to DirectShape as usual.
    if (mode == RevitReceiveMode.NativeTekla)
    {
      // A whole Tekla grid system fans out into N native DB.Grid elements - a different result
      // shape than every other category (see GridSystemWrapper), so it's checked separately here
      // rather than folded into TryNativeTeklaConvert's single-DB.Element-returning contract.
      if (
        target is TeklaObject { type: "Grid" } teklaGridObject
        && TryNativeTeklaGridConvert(teklaGridObject) is { } gridSystem
      )
      {
        return gridSystem;
      }

      if (TryNativeTeklaConvert(target) is { } nativeTeklaElement)
      {
        return nativeTeklaElement;
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

  private DB.Element? TryNativeRevitConvert(Base target)
  {
    // locale-independent (e.g. "OST_Walls") - category names from Element.Category.Name are
    // localized to the sending Revit's UI language and can't be used for dispatch here.
    string category = target["builtInCategory"] as string ?? string.Empty;

    (string Category, Func<DB.Element> Convert)[] converters =
    [
      ("OST_Grids", () => _gridConverter.Convert(target)),
      ("OST_StructuralFraming", () => _beamConverter.Convert(target)),
      ("OST_StructuralColumns", () => _columnConverter.Convert(target)),
      ("OST_Walls", () => _wallConverter.Convert(target)),
      ("OST_Floors", () => _floorConverter.Convert(target)),
      ("OST_StructuralFoundation", () => _foundationConverter.Convert(target)),
      ("OST_Roofs", () => _roofConverter.Convert(target)),
    ];

    foreach (var (expectedCategory, convert) in converters)
    {
      if (
        category.Equals(expectedCategory, StringComparison.OrdinalIgnoreCase)
        && TryNativeConvert(target, category, convert) is { } element
      )
      {
        return element;
      }
    }

    if (category.ContainsOrdinalIgnoreCase("Opening"))
    {
      return TryNativeConvert(target, category, () => _openingConverter.Convert(target));
    }

    return null;
  }

  private DB.Element? TryNativeTeklaConvert(Base target)
  {
    if (target is not TeklaObject teklaObject)
    {
      return null;
    }

    // A wall-panel boolean-cut child is tagged with "parentApplicationId" instead of a recognized
    // Tekla class (see ModelObjectToSpeckleConverter) - check this BEFORE the type=="Beam" gate below,
    // since the cutter's own native Tekla type can itself be "Beam" (a beam-shaped void), which would
    // otherwise fall through to class-based dispatch and misfire as a real duplicate structural element.
    if (teklaObject.properties.GetOrDefault("parentApplicationId") is string)
    {
      return TryNativeConvert(target, "Opening", () => _openingConverter.Convert(target));
    }

    // "PolyBeam" is a curved beam or wall panel (Tekla's only way to represent an arc - see
    // LocationExtractor.TryGetArcLocation) - the class-based routing below is already class-driven,
    // not type-driven, so admitting this type here is enough to route curved beams/walls the same
    // way as their straight counterparts. "ContourPlate" is a slab (Class=CONCRETE_SLAB).
    if (teklaObject.type is not ("Beam" or "PolyBeam" or "ContourPlate"))
    {
      return null;
    }

    string? teklaClass = teklaObject.properties.GetOrDefault("class") as string;
    return TeklaClassCategoryResolver.Resolve(teklaClass) switch
    {
      DB.BuiltInCategory.OST_StructuralColumns => TryNativeConvert(
        target,
        "Column",
        () => _columnConverter.Convert(target)
      ),
      DB.BuiltInCategory.OST_StructuralFoundation => TryNativeConvert(
        target,
        "Foundation",
        () => _foundationConverter.Convert(target)
      ),
      DB.BuiltInCategory.OST_Walls => TryNativeConvert(target, "Wall", () => _wallConverter.Convert(target)),
      DB.BuiltInCategory.OST_Floors => TryNativeConvert(target, "Floor", () => _floorConverter.Convert(target)),
      _ => TryNativeConvert(target, teklaObject.type, () => _beamConverter.Convert(target)),
    };
  }

  /// <summary>
  /// Attempts to reconstruct a whole Tekla grid system as native Grid elements. Mirrors
  /// TryNativeConvert's try/catch/log-and-fall-back-to-DirectShape shape, but returns a
  /// GridSystemWrapper (many elements) instead of a single DB.Element.
  /// </summary>
#pragma warning disable CA1031
  private GridSystemWrapper? TryNativeTeklaGridConvert(TeklaObject target)
  {
    try
    {
      return new GridSystemWrapper(_teklaGridSystemConverter.Convert(target));
    }
    catch (Exception ex) when (!ex.IsFatal())
    {
      _logger.LogWarning(
        ex,
        "Native Grid conversion failed for '{Name}' (id: {Id}) - falling back to DirectShape.",
        target["name"],
        target.applicationId ?? target.id
      );
      return null;
    }
  }
#pragma warning restore CA1031

  /// <summary>
  /// Attempts a category-specific native conversion. On failure, logs the underlying exception
  /// (which would otherwise be silently swallowed) and returns null so the caller can fall through
  /// to DirectShape.
  /// </summary>
#pragma warning disable CA1031
  private DB.Element? TryNativeConvert(Base target, string category, Func<DB.Element> convert)
  {
    try
    {
      return convert();
    }
    catch (Exception ex) when (!ex.IsFatal())
    {
      object? location = target["location"];
      _logger.LogWarning(
        ex,
        "Native {Category} conversion failed for '{Name}' (id: {Id}, locationType: {LocationType}, locationSpeckleType: {LocationSpeckleType}) - falling back to DirectShape.",
        category,
        target["name"],
        target.applicationId ?? target.id,
        location?.GetType().FullName ?? "null",
        (location as Base)?.speckle_type ?? "n/a"
      );
      return null;
    }
  }
#pragma warning restore CA1031
#pragma warning restore CA2000
}
