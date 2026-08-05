using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.TeklaShared.Helpers;
using Speckle.Converters.TeklaShared.Helpers.ProfileMapping;
using Speckle.Objects.Data;
using Speckle.Sdk.Common;
using Speckle.Sdk.Common.Exceptions;

namespace Speckle.Converters.TeklaShared.ToHost;

/// <summary>
/// Converts a Revit Wall (arriving as a <see cref="RevitObject"/>) into a Tekla "panel" running
/// along the wall's baseline, with a rectangular profile sized by the wall's thickness (width)
/// and height: straight walls become a <see cref="TSM.Beam"/>, curved (arc) walls a
/// <see cref="TSM.PolyBeam"/> following the true arc (via a CHAMFER_ARC_POINT contour point).
/// No profile-catalog mapping is involved - the rectangular section comes directly from the
/// wall's own dimensions. Base elevation and height are taken from the captured displayValue
/// geometry when available (exact, including base offsets and attached tops); the height
/// parameter is the fallback.
///
/// Straight walls are matched/updated in place via <see cref="TeklaExistingBeamIndex"/>, mirroring
/// the Beam/Column/Foundation mechanism. Curved walls are not - <see cref="TeklaExistingBeamIndex"/>
/// only ever scans <see cref="TSM.Beam"/>, not <see cref="TSM.PolyBeam"/>, so a re-sent curved wall
/// always inserts a fresh PolyBeam; any previously-created one is left unclaimed and swept up by the
/// deletion pass, same accepted policy as multi-segment strip footings.
/// </summary>
public class RevitWallToTeklaBeamConverter : ITypedConverter<RevitObject, TSM.Part>
{
  private const double DEFAULT_WALL_THICKNESS_MM = 200;
  private const double DEFAULT_WALL_HEIGHT_MM = 3000;
  private const string DEFAULT_MATERIAL = "C30/37";

  private readonly ITypedConverter<SOG.Point, TG.Point> _pointConverter;
  private readonly IConverterSettingsStore<TeklaConversionSettings> _settingsStore;
  private readonly RevitProfileMaterialMappingProvider _mappingProvider;
  private readonly TeklaCatalogValidator _catalogValidator;
  private readonly ConversionWarningCollector _warnings;
  private readonly TeklaExistingBeamIndex _existingBeamIndex;
  private readonly ILogger<RevitWallToTeklaBeamConverter> _logger;

  public RevitWallToTeklaBeamConverter(
    ITypedConverter<SOG.Point, TG.Point> pointConverter,
    IConverterSettingsStore<TeklaConversionSettings> settingsStore,
    RevitProfileMaterialMappingProvider mappingProvider,
    TeklaCatalogValidator catalogValidator,
    ConversionWarningCollector warnings,
    TeklaExistingBeamIndex existingBeamIndex,
    ILogger<RevitWallToTeklaBeamConverter> logger
  )
  {
    _pointConverter = pointConverter;
    _settingsStore = settingsStore;
    _mappingProvider = mappingProvider;
    _catalogValidator = catalogValidator;
    _warnings = warnings;
    _existingBeamIndex = existingBeamIndex;
    _logger = logger;
  }

  public TSM.Part Convert(RevitObject target)
  {
    // Tekla model coordinates are always millimeters (see PointToHostConverter), regardless of the
    // Tekla Options>Units display setting captured in _settingsStore.Current.SpeckleUnits.
    double scale = RevitPropertyReader.GetUnitScaleFactor(target.units, Units.Millimeters);

    SOG.Point planStart;
    SOG.Point planEnd;
    SOG.Point? planArcMid = null;
    switch (target["location"])
    {
      case SOG.Line line:
        var scaledLine = RevitPropertyReader.ScaleLine(line, scale);
        planStart = scaledLine.start;
        planEnd = scaledLine.end;
        break;
      // Curved wall: PolyBeam panel following the true arc via a CHAMFER_ARC_POINT through the
      // arc's midpoint - exact, no chord approximation.
      case SOG.Arc arc:
        planStart = RevitPropertyReader.ScalePoint(arc.startPoint, scale);
        planArcMid = RevitPropertyReader.ScalePoint(arc.midPoint, scale);
        planEnd = RevitPropertyReader.ScalePoint(arc.endPoint, scale);
        break;
      default:
        throw new ConversionException(
          $"Revit Wall '{target.family} : {target.type}' (name '{target.name}') has an unsupported location of "
            + $"type '{target.location?.GetType().Name ?? "null"}' - Tekla panel conversion needs a line or arc."
        );
    }

    // Tekla model coordinates are always millimeters (see PointToHostConverter) - this is
    // intentionally a no-op scale, kept only so the surrounding math below reads uniformly.
    string units = Units.Millimeters;
    double mmToModel = RevitPropertyReader.GetUnitScaleFactor(Units.Millimeters, units);

    // Vertical extent: prefer the captured mesh geometry (exact - reflects base offsets and
    // attached/stepped tops); fall back to the height parameter at the baseline's own elevation.
    double baseZ;
    double heightMm;
    if (RevitPropertyReader.TryGetDisplayValueBBoxMm(target, out var bbox) && bbox.SizeZ > 1e-3)
    {
      baseZ = bbox.MinZ * mmToModel;
      heightMm = bbox.SizeZ;
    }
    else
    {
      baseZ = Math.Min(planStart.z, planEnd.z);
      heightMm = GetWallHeightParamMm(target) ?? DEFAULT_WALL_HEIGHT_MM;
      _warnings.Add(
        target.id,
        $"Wall has no captured geometry; height {heightMm:0.#}mm taken from parameters/defaults."
      );
    }

    double thicknessMm = GetWallThicknessMm(target);

    var start = _pointConverter.Convert(new SOG.Point(planStart.x, planStart.y, baseZ, units));
    var end = _pointConverter.Convert(new SOG.Point(planEnd.x, planEnd.y, baseZ, units));

    string profile = $"{heightMm:0.#}*{thicknessMm:0.#}";

    string? candidate = null;
    if (
      RevitPropertyReader.TryGetStructuralMaterialName(target, out var revitMaterialName)
      && _mappingProvider.TryGetMaterial(revitMaterialName!, out var mapped)
    )
    {
      candidate = mapped;
    }
    var (material, materialWarning) = _catalogValidator.ValidateOrFallback(
      candidate,
      DEFAULT_MATERIAL,
      isProfile: false
    );
    if (materialWarning != null)
    {
      _warnings.Add(target.id, materialWarning);
    }

    // Curved walls always insert a fresh PolyBeam - see class remarks (TeklaExistingBeamIndex only
    // ever scans TSM.Beam).
    if (planArcMid is { } arcMid)
    {
      var mid = _pointConverter.Convert(new SOG.Point(arcMid.x, arcMid.y, baseZ, units));
      TSM.Part polyBeam = RevitColumnBeamToTeklaBeamConverter.CreateArcPolyBeam(start, mid, end);
      ApplyCommonProperties(polyBeam, material, profile, target.name);
      polyBeam.Insert();
      StampOrigin(polyBeam, target);
      return polyBeam;
    }

    // Baseline at the wall base, matching Tekla's "Betonwand" panel defaults: Plane=MIDDLE
    // (centers the thickness on the baseline), Rotation=TOP and Depth=FRONT (extrudes the
    // profile height upward from the baseline).
    if (_existingBeamIndex.TryFindExisting(target.applicationId ?? target.id, out var existingBeam))
    {
      existingBeam!.StartPoint = start;
      existingBeam.EndPoint = end;
      ApplyCommonProperties(existingBeam, material, profile, target.name);
      existingBeam.Modify();
      StampOrigin(existingBeam, target);
      return existingBeam;
    }

    var beam = new TSM.Beam(start, end);
    ApplyCommonProperties(beam, material, profile, target.name);
    beam.Insert();
    StampOrigin(beam, target);
    return beam;
  }

  private static void ApplyCommonProperties(TSM.Part beam, string material, string profile, string name)
  {
    beam.Profile.ProfileString = profile;
    beam.Class = TeklaStandardClasses.CONCRETE_PANEL;
    beam.Position.Plane = TSM.Position.PlaneEnum.MIDDLE;
    beam.Position.Rotation = TSM.Position.RotationEnum.TOP;
    beam.Position.Depth = TSM.Position.DepthEnum.FRONT;
    beam.Name = name.Length > 0 ? name : "Wall";
    beam.Material.MaterialString = material;
  }

  private void StampOrigin(TSM.Part beam, RevitObject target)
  {
    string? originApplicationId = target.applicationId ?? target.id;
    if (originApplicationId is not null)
    {
      TeklaOriginIdentifier.Set(beam, originApplicationId, _logger);
    }
  }

  private static double GetWallThicknessMm(RevitObject target) =>
    RevitPropertyReader.TryGetParameter(target, "Type Parameters", "WALL_ATTR_WIDTH_PARAM", out var param)
    && RevitPropertyReader.TryToDouble(param!.GetOrDefault("value"), out var value)
      ? RevitPropertyReader.ConvertToMm(value, param.GetOrDefault("unitsTypeId") as string)
      : DEFAULT_WALL_THICKNESS_MM;

  private static double? GetWallHeightParamMm(RevitObject target) =>
    RevitPropertyReader.TryGetParameter(target, "Instance Parameters", "WALL_USER_HEIGHT_PARAM", out var param)
    && RevitPropertyReader.TryToDouble(param!.GetOrDefault("value"), out var value)
      ? RevitPropertyReader.ConvertToMm(value, param.GetOrDefault("unitsTypeId") as string)
      : null;

  public object Convert(object target) => Convert((RevitObject)target);
}
