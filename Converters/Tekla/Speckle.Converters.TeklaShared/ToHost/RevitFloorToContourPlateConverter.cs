using Speckle.Converters.Common;
using Speckle.Converters.TeklaShared.Helpers;
using Speckle.Converters.TeklaShared.Helpers.ProfileMapping;
using Speckle.Objects.Data;
using Speckle.Sdk.Common.Exceptions;

namespace Speckle.Converters.TeklaShared.ToHost;

/// <summary>
/// Converts a Revit Floor or slab-like foundation (arriving as a <see cref="RevitObject"/>) into
/// a Tekla <see cref="TSM.ContourPlate"/> via its boundary polycurve. Thickness comes from the
/// floor type parameter, falling back to the captured geometry's Z-extent (which also covers
/// foundation slabs, whose thickness parameter differs from floors').
/// </summary>
public class RevitFloorToContourPlateConverter : ITypedConverter<RevitObject, TSM.ContourPlate>
{
  private const double DEFAULT_FLOOR_THICKNESS_MM = 200;
  private const string DEFAULT_MATERIAL = "C30/37";

  private readonly ITypedConverter<SOG.Point, TG.Point> _pointConverter;
  private readonly IConverterSettingsStore<TeklaConversionSettings> _settingsStore;
  private readonly RevitProfileMaterialMappingProvider _mappingProvider;
  private readonly TeklaCatalogValidator _catalogValidator;
  private readonly ConversionWarningCollector _warnings;

  public RevitFloorToContourPlateConverter(
    ITypedConverter<SOG.Point, TG.Point> pointConverter,
    IConverterSettingsStore<TeklaConversionSettings> settingsStore,
    RevitProfileMaterialMappingProvider mappingProvider,
    TeklaCatalogValidator catalogValidator,
    ConversionWarningCollector warnings
  )
  {
    _pointConverter = pointConverter;
    _settingsStore = settingsStore;
    _mappingProvider = mappingProvider;
    _catalogValidator = catalogValidator;
    _warnings = warnings;
  }

  public TSM.ContourPlate Convert(RevitObject target)
  {
    double scale = RevitPropertyReader.GetUnitScaleFactor(target.units, _settingsStore.Current.SpeckleUnits);

    if (target["location"] is not SOG.Polycurve polycurve)
    {
      throw new ConversionException(
        $"Revit {target.category} '{target.family} : {target.type}' (name '{target.name}') requires a polycurve "
          + $"boundary location for Tekla slab conversion (got '{target.location?.GetType().Name ?? "null"}')."
      );
    }

    var contour = new TSM.Contour();
    foreach (var (point, chamferRadius) in RevitPropertyReader.PolycurveToScaledPoints(polycurve, scale))
    {
      var chamfer =
        chamferRadius > 0
          ? new TSM.Chamfer(chamferRadius, chamferRadius, TSM.Chamfer.ChamferTypeEnum.CHAMFER_ROUNDING)
          : new TSM.Chamfer();
      contour.AddContourPoint(new TSM.ContourPoint(_pointConverter.Convert(point), chamfer));
    }

    var plate = new TSM.ContourPlate { Contour = contour };

    // Tekla parametric plate profiles ("PL250") are in millimeters.
    plate.Profile.ProfileString = $"PL{GetThicknessMm(target):0}";

    var candidates = new List<string?>();
    if (RevitPropertyReader.TryGetStructuralMaterialName(target, out var revitMaterialName))
    {
      if (_mappingProvider.TryGetMaterial(revitMaterialName!, out var mapped))
      {
        candidates.Add(mapped);
      }
      candidates.Add(revitMaterialName);
    }
    var (material, materialWarning) = _catalogValidator.ValidateFirstOrFallback(
      candidates,
      DEFAULT_MATERIAL,
      isProfile: false
    );
    plate.Material.MaterialString = material;
    if (materialWarning != null)
    {
      _warnings.Add(target.id, materialWarning);
    }

    // Standard class (color): foundation slabs delegated here keep the foundation class; other
    // slabs split concrete slab vs steel plate by material.
    plate.Class =
      target["builtInCategory"] as string == "OST_StructuralFoundation"
        ? TeklaStandardClasses.FOUNDATION
        : TeklaStandardClasses.ForSlab(material);

    plate.Name = target.name.Length > 0 ? target.name : "Floor";

    plate.Insert();
    return plate;
  }

  // Floor type thickness parameter first; foundation slabs (and floor types where the parameter
  // isn't captured) fall back to the captured geometry's Z-extent.
  private static double GetThicknessMm(RevitObject target)
  {
    if (
      RevitPropertyReader.TryGetParameter(target, "Type Parameters", "FLOOR_ATTR_DEFAULT_THICKNESS_PARAM", out var param)
      && RevitPropertyReader.TryToDouble(param!.GetOrDefault("value"), out var value)
    )
    {
      double mm = RevitPropertyReader.ConvertToMm(value, param.GetOrDefault("unitsTypeId") as string);
      if (mm > 0)
      {
        return mm;
      }
    }

    if (RevitPropertyReader.TryGetDisplayValueBBoxMm(target, out var bbox) && bbox.SizeZ > 1)
    {
      return bbox.SizeZ;
    }

    return DEFAULT_FLOOR_THICKNESS_MM;
  }

  public object Convert(object target) => Convert((RevitObject)target);
}
