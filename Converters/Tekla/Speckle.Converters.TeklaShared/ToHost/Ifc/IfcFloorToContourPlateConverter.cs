using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.TeklaShared.Helpers;
using Speckle.Converters.TeklaShared.Helpers.ProfileMapping;
using Speckle.Objects.Data;
using Speckle.Sdk.Common.Exceptions;

namespace Speckle.Converters.TeklaShared.ToHost.Ifc;

/// <summary>
/// Converts an IFC-enriched <see cref="DataObject"/> floor/foundation-slab (builtInCategory
/// <c>OST_Floors</c> or <c>OST_StructuralFoundation</c>) into a Tekla <see cref="TSM.ContourPlate"/>.
/// Mirrors <see cref="RevitFloorToContourPlateConverter"/>, but reads the enricher's boundary as a
/// plain <see cref="SOG.Polyline"/> (every corner straight - the IFC boundary/mesh extractors never
/// produce arcs) rather than a <see cref="SOG.Polycurve"/>, and its already-resolved "PL{thickness}"
/// profile string directly - no chamfer handling, no parameter lookups, no unit scaling needed.
/// </summary>
public class IfcFloorToContourPlateConverter : ITypedConverter<DataObject, TSM.ContourPlate>
{
  private const double DEFAULT_FLOOR_THICKNESS_MM = 200;
  private const string DEFAULT_MATERIAL = "C30/37";
  private static readonly string DEFAULT_PROFILE = FormattableString.Invariant($"PL{DEFAULT_FLOOR_THICKNESS_MM:0}");

  private readonly TeklaCatalogValidator _catalogValidator;
  private readonly ConversionWarningCollector _warnings;
  private readonly TeklaExistingContourPlateIndex _existingContourPlateIndex;
  private readonly ILogger<IfcFloorToContourPlateConverter> _logger;

  public IfcFloorToContourPlateConverter(
    TeklaCatalogValidator catalogValidator,
    ConversionWarningCollector warnings,
    TeklaExistingContourPlateIndex existingContourPlateIndex,
    ILogger<IfcFloorToContourPlateConverter> logger
  )
  {
    _catalogValidator = catalogValidator;
    _warnings = warnings;
    _existingContourPlateIndex = existingContourPlateIndex;
    _logger = logger;
  }

  public TSM.ContourPlate Convert(DataObject target)
  {
    if (target["location"] is not SOG.Polyline polyline || polyline.value.Count < 9)
    {
      throw new ConversionException(
        $"IFC-sourced element applicationId={target.applicationId} requires a polyline boundary location "
          + $"for Tekla slab conversion (got '{target["location"]?.GetType().Name ?? "null"}')."
      );
    }

    var contour = new TSM.Contour();
    for (int i = 0; i < polyline.value.Count; i += 3)
    {
      contour.AddContourPoint(
        new TSM.ContourPoint(
          new TG.Point(polyline.value[i], polyline.value[i + 1], polyline.value[i + 2]),
          new TSM.Chamfer()
        )
      );
    }

    var (profile, profileWarning) = _catalogValidator.ValidateOrFallback(
      target["profile"] as string,
      DEFAULT_PROFILE,
      isProfile: true
    );
    if (profileWarning != null)
    {
      _warnings.Add(target.id, profileWarning);
    }

    var (material, materialWarning) = _catalogValidator.ValidateOrFallback(null, DEFAULT_MATERIAL, isProfile: false);
    if (materialWarning != null)
    {
      _warnings.Add(target.id, materialWarning);
    }

    string plateClass =
      target["builtInCategory"] as string == "OST_StructuralFoundation"
        ? TeklaStandardClasses.FOUNDATION
        : TeklaStandardClasses.ForSlab(material);
    string plateName = target.name.Length > 0 ? IfcNameCleaner.StripTrailingTag(target.name) : "Floor";

    if (
      _existingContourPlateIndex.TryFindExisting(target.applicationId ?? target.id, out var existingPlate)
      && existingPlate is not null
    )
    {
      existingPlate.Contour = contour;
      existingPlate.Profile.ProfileString = profile;
      existingPlate.Material.MaterialString = material;
      existingPlate.Class = plateClass;
      existingPlate.Name = plateName;
      // Set AFTER Contour is assigned - Tekla derives the depth axis from the contour's own plane.
      // The IFC boundary is captured from the TOP face (mirrors Revit's own floor extraction), so
      // the plate's thickness must extend BEHIND (below) that contour.
      existingPlate.Position.Depth = TSM.Position.DepthEnum.BEHIND;
      existingPlate.Modify();
      StampOrigin(existingPlate, target);
      return existingPlate;
    }

    var plate = new TSM.ContourPlate
    {
      Contour = contour,
      Class = plateClass,
      Name = plateName,
    };
    plate.Profile.ProfileString = profile;
    plate.Material.MaterialString = material;
    plate.Position.Depth = TSM.Position.DepthEnum.BEHIND;

    plate.Insert();
    StampOrigin(plate, target);
    return plate;
  }

  private void StampOrigin(TSM.ContourPlate plate, DataObject target)
  {
    string? originApplicationId = target.applicationId ?? target.id;
    if (originApplicationId is not null)
    {
      TeklaOriginIdentifier.Set(plate, originApplicationId, _logger);
    }
  }

  public object Convert(object target) => Convert((DataObject)target);
}
