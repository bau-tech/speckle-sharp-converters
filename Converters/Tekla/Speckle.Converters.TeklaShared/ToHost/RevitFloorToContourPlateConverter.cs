using Microsoft.Extensions.Logging;
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
  private readonly TeklaExistingContourPlateIndex _existingContourPlateIndex;
  private readonly ILogger<RevitFloorToContourPlateConverter> _logger;

  public RevitFloorToContourPlateConverter(
    ITypedConverter<SOG.Point, TG.Point> pointConverter,
    IConverterSettingsStore<TeklaConversionSettings> settingsStore,
    RevitProfileMaterialMappingProvider mappingProvider,
    TeklaCatalogValidator catalogValidator,
    ConversionWarningCollector warnings,
    TeklaExistingContourPlateIndex existingContourPlateIndex,
    ILogger<RevitFloorToContourPlateConverter> logger
  )
  {
    _pointConverter = pointConverter;
    _settingsStore = settingsStore;
    _mappingProvider = mappingProvider;
    _catalogValidator = catalogValidator;
    _warnings = warnings;
    _existingContourPlateIndex = existingContourPlateIndex;
    _logger = logger;
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

    // Tekla parametric plate profiles ("PL250") are in millimeters - mapping-table entry first
    // (mirrors RevitColumnBeamToTeklaBeamConverter.ResolveProfile), thickness-derived formula as
    // fallback, same "candidate list, first valid one wins" pattern used throughout this project.
    var profileCandidates = new List<string?>();
    if (_mappingProvider.TryGetProfile(target.family, target.type, out var mappedProfile))
    {
      profileCandidates.Add(mappedProfile);
    }
    double thicknessMm = GetThicknessMm(target);
    profileCandidates.Add($"PL{thicknessMm:0}");
    var (profile, profileWarning) = _catalogValidator.ValidateFirstOrFallback(
      profileCandidates,
      $"PL{DEFAULT_FLOOR_THICKNESS_MM:0}",
      isProfile: true
    );
    if (profileWarning != null)
    {
      _warnings.Add(target.id, profileWarning);
    }

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
    if (materialWarning != null)
    {
      _warnings.Add(target.id, materialWarning);
    }

    // Standard class (color): foundation slabs delegated here keep the foundation class; other
    // slabs split concrete slab vs steel plate by material.
    string plateClass =
      target["builtInCategory"] as string == "OST_StructuralFoundation"
        ? TeklaStandardClasses.FOUNDATION
        : TeklaStandardClasses.ForSlab(material);
    string plateName = target.name.Length > 0 ? target.name : "Floor";

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
      // Revit's floor boundary is captured from the TOP face (see ExtractFloorBoundaryAndOpenings),
      // so the plate's thickness must extend BEHIND (below) that contour, not in FRONT of it -
      // otherwise the slab bakes floating above where it should sit, offset by its own thickness.
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
    // See comment above (update branch) - Revit's floor contour is its TOP face boundary, so the
    // plate must be built BEHIND (below) it.
    plate.Position.Depth = TSM.Position.DepthEnum.BEHIND;

    plate.Insert();
    StampOrigin(plate, target);
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

  private void StampOrigin(TSM.ContourPlate plate, RevitObject target)
  {
    string? originApplicationId = target.applicationId ?? target.id;
    if (originApplicationId is not null)
    {
      TeklaOriginIdentifier.Set(plate, originApplicationId, _logger);
    }
  }

  public object Convert(object target) => Convert((RevitObject)target);
}
