using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Converters.RevitShared.Helpers;
using Speckle.Converters.RevitShared.Settings;
using Speckle.Sdk.Common;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.RevitShared.ToHost;

/// <summary>
/// Shared FamilySymbol/Level resolution and FamilyInstance creation logic for structural framing
/// elements (Beams, Columns) received as native Revit elements.
/// </summary>
public class StructuralFramingHelper
{
  private readonly IConverterSettingsStore<RevitConversionSettings> _settingsStore;
  private readonly RevitElementTypeResolver _typeResolver;
  private readonly RevitToHostCacheSingleton _cache;
  private readonly ITypedConverter<SOG.Line, DB.Line> _lineConverter;
  private readonly ITypedConverter<SOG.Point, DB.XYZ> _pointConverter;
  private readonly ITypedConverter<SOG.Arc, DB.Arc> _arcConverter;

  public StructuralFramingHelper(
    IConverterSettingsStore<RevitConversionSettings> settingsStore,
    RevitElementTypeResolver typeResolver,
    RevitToHostCacheSingleton cache,
    ITypedConverter<SOG.Line, DB.Line> lineConverter,
    ITypedConverter<SOG.Point, DB.XYZ> pointConverter,
    ITypedConverter<SOG.Arc, DB.Arc> arcConverter
  )
  {
    _settingsStore = settingsStore;
    _typeResolver = typeResolver;
    _cache = cache;
    _lineConverter = lineConverter;
    _pointConverter = pointConverter;
    _arcConverter = arcConverter;
  }

  /// <summary>
  /// Resolves the FamilySymbol and Level for <paramref name="target"/>, creates a FamilyInstance from
  /// its location (point or line), and registers it in the receive cache for later lookup.
  /// </summary>
  public DB.FamilyInstance Create(Base target, DB.BuiltInCategory category, DB.Structure.StructuralType structuralType)
  {
    var doc = _settingsStore.Current.Document;

    DB.FamilySymbol symbol =
      _typeResolver.FindFamilySymbol(target["family"] as string, target["type"] as string, category)
      ?? throw new ConversionException($"No FamilySymbol found for category '{category}' in the document.");

    DB.Level level =
      _typeResolver.FindLevel(target["level"] as string)
      ?? throw new ConversionException("No levels found in the document.");

    DB.FamilyInstance instance = target["location"] switch
    {
      SOG.Line line => doc.Create.NewFamilyInstance(_lineConverter.Convert(line), symbol, level, structuralType),
      SOG.Arc arc => doc.Create.NewFamilyInstance(_arcConverter.Convert(arc), symbol, level, structuralType),
      SOG.Point point => doc.Create.NewFamilyInstance(_pointConverter.Convert(point), symbol, level, structuralType),
      _ => throw new ConversionException($"Native {structuralType} requires a line, arc, or point location."),
    };

    if (
      RevitElementPropertyApplicator.TryGetAngleInRadians(
        target,
        "Instance Parameters",
        "STRUCTURAL_BEND_DIR_ANGLE",
        out double rotation
      )
    )
    {
      RevitElementPropertyApplicator.TrySetDouble(instance, DB.BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE, rotation);
    }

    string cacheKey = target.applicationId ?? target.id.NotNull();
    _cache.ReceivedElementsByApplicationId[cacheKey] = instance;

    return instance;
  }

  /// <summary>
  /// Re-applies a point-based placement's captured plan rotation (e.g. for structural columns).
  /// Must be called AFTER any level/offset constraints have been applied (e.g.
  /// <c>ColumnToHostConverter.ApplyVerticalExtents</c>) - setting those on a rotated instance can
  /// cause Revit to recompute the LocationPoint and shift the element away from its insertion point,
  /// so rotation needs to be the final placement step.
  /// </summary>
  public void ReapplyPlacementRotation(Base target, DB.FamilyInstance instance)
  {
    if (
      target["location"] is not Base locationBase
      || !RevitElementPropertyApplicator.TryToDouble(locationBase["rotation"], out double placementRotation)
    )
    {
      return;
    }

    _settingsStore.Current.Document.Regenerate();

    if (instance.Location is not DB.LocationPoint locationPoint)
    {
      return;
    }

    // Rotate() applies a delta, but the captured value is an absolute angle - rotate by the
    // difference from the instance's current (freshly-placed) rotation to land on that angle.
    double rotationDelta = placementRotation - locationPoint.Rotation;
    if (rotationDelta != 0)
    {
      using DB.Line axis = DB.Line.CreateBound(locationPoint.Point, locationPoint.Point + DB.XYZ.BasisZ);
      _ = locationPoint.Rotate(axis, rotationDelta);
    }
  }
}
