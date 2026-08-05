using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.TeklaShared.Helpers;
using Speckle.Converters.TeklaShared.Helpers.ProfileMapping;
using Speckle.Objects.Data;
using Speckle.Sdk.Common.Exceptions;

namespace Speckle.Converters.TeklaShared.ToHost.Ifc;

/// <summary>
/// Converts an IFC-enriched <see cref="DataObject"/> wall (builtInCategory <c>OST_Walls</c>) into
/// a Tekla panel, mirroring <see cref="RevitWallToTeklaBeamConverter"/> but reading the enricher's
/// already-resolved "{height}*{thickness}" profile string directly - no material/thickness
/// parameter lookups needed, and no unit scaling (a plain <see cref="DataObject"/>'s geometry is
/// already in millimeters).
/// </summary>
public class IfcWallToTeklaBeamConverter : ITypedConverter<DataObject, TSM.Part>
{
  private const double DEFAULT_WALL_THICKNESS_MM = 200;
  private const double DEFAULT_WALL_HEIGHT_MM = 3000;
  private const string DEFAULT_MATERIAL = "C30/37";

  private static readonly string DEFAULT_PROFILE = FormattableString.Invariant(
    $"{DEFAULT_WALL_HEIGHT_MM:0.#}*{DEFAULT_WALL_THICKNESS_MM:0.#}"
  );

  private readonly ITypedConverter<SOG.Point, TG.Point> _pointConverter;
  private readonly TeklaCatalogValidator _catalogValidator;
  private readonly ConversionWarningCollector _warnings;
  private readonly TeklaExistingBeamIndex _existingBeamIndex;
  private readonly ILogger<IfcWallToTeklaBeamConverter> _logger;

  public IfcWallToTeklaBeamConverter(
    ITypedConverter<SOG.Point, TG.Point> pointConverter,
    TeklaCatalogValidator catalogValidator,
    ConversionWarningCollector warnings,
    TeklaExistingBeamIndex existingBeamIndex,
    ILogger<IfcWallToTeklaBeamConverter> logger
  )
  {
    _pointConverter = pointConverter;
    _catalogValidator = catalogValidator;
    _warnings = warnings;
    _existingBeamIndex = existingBeamIndex;
    _logger = logger;
  }

  public TSM.Part Convert(DataObject target)
  {
    TG.Point start;
    TG.Point end;
    TG.Point? arcMid = null;
    switch (target["location"])
    {
      case SOG.Line line:
        start = _pointConverter.Convert(line.start);
        end = _pointConverter.Convert(line.end);
        break;
      case SOG.Arc arc:
        start = _pointConverter.Convert(arc.startPoint);
        arcMid = _pointConverter.Convert(arc.midPoint);
        end = _pointConverter.Convert(arc.endPoint);
        break;
      default:
        throw new ConversionException(
          $"IFC-sourced wall applicationId={target.applicationId} has an unsupported location of type "
            + $"'{target["location"]?.GetType().Name ?? "null"}' - Tekla panel conversion needs a line or arc."
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

    string name = target.name.Length > 0 ? IfcNameCleaner.StripTrailingTag(target.name) : "Wall";

    // Curved walls always insert a fresh PolyBeam, matching RevitWallToTeklaBeamConverter's own
    // policy (TeklaExistingBeamIndex only ever scans TSM.Beam, never TSM.PolyBeam).
    if (arcMid is { } mid)
    {
      TSM.Part polyBeam = RevitColumnBeamToTeklaBeamConverter.CreateArcPolyBeam(start, mid, end);
      ApplyCommonProperties(polyBeam, material, profile, name);
      polyBeam.Insert();
      StampOrigin(polyBeam, target);
      return polyBeam;
    }

    if (_existingBeamIndex.TryFindExisting(target.applicationId ?? target.id, out var existingBeam))
    {
      existingBeam!.StartPoint = start;
      existingBeam.EndPoint = end;
      ApplyCommonProperties(existingBeam, material, profile, name);
      existingBeam.Modify();
      StampOrigin(existingBeam, target);
      return existingBeam;
    }

    var beam = new TSM.Beam(start, end);
    ApplyCommonProperties(beam, material, profile, name);
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
    beam.Name = name;
    beam.Material.MaterialString = material;
  }

  private void StampOrigin(TSM.Part beam, DataObject target)
  {
    string? originApplicationId = target.applicationId ?? target.id;
    if (originApplicationId is not null)
    {
      TeklaOriginIdentifier.Set(beam, originApplicationId, _logger);
    }
  }

  public object Convert(object target) => Convert((DataObject)target);
}
