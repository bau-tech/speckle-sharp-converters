using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.TeklaShared.Helpers;
using Speckle.Converters.TeklaShared.Helpers.ProfileMapping;
using Speckle.Objects.Data;
using Speckle.Sdk.Common.Exceptions;

namespace Speckle.Converters.TeklaShared.ToHost.Ifc;

/// <summary>
/// Converts an IFC-enriched <see cref="DataObject"/> foundation (builtInCategory
/// <c>OST_StructuralFoundation</c>) into a native Tekla part. Mirrors
/// <see cref="RevitFoundationToTeklaConverter"/>, but the dispatch is simpler since the IFC
/// enricher's own extraction already distinguishes the three foundation shapes cleanly by location
/// type: a vertical <see cref="SOG.Line"/> (isolated pad footing/pile cap, with a resolved
/// "{width}*{depth}" profile), a bare <see cref="SOG.Point"/> (pile - no profile, resolved only via
/// the ifcTypeName mapping table), or a closed <see cref="SOG.Polyline"/> boundary (foundation
/// mat/slab - delegates to <see cref="IfcFloorToContourPlateConverter"/>, exactly like the Revit
/// converter delegates to its own floor converter).
/// </summary>
public class IfcFoundationToTeklaConverter : ITypedConverter<DataObject, TSM.ModelObject>
{
  private const string DEFAULT_MATERIAL = "C30/37";
  private const double DEFAULT_PILE_LENGTH_MM = 6000;
  private const string DEFAULT_PILE_PROFILE = "D400";

  private readonly ITypedConverter<SOG.Line, TG.LineSegment> _lineConverter;
  private readonly ITypedConverter<SOG.Point, TG.Point> _pointConverter;
  private readonly ITypedConverter<DataObject, TSM.ContourPlate> _floorConverter;
  private readonly IfcProfileMappingProvider _mappingProvider;
  private readonly TeklaCatalogValidator _catalogValidator;
  private readonly ConversionWarningCollector _warnings;
  private readonly TeklaExistingBeamIndex _existingBeamIndex;
  private readonly ILogger<IfcFoundationToTeklaConverter> _logger;

  public IfcFoundationToTeklaConverter(
    ITypedConverter<SOG.Line, TG.LineSegment> lineConverter,
    ITypedConverter<SOG.Point, TG.Point> pointConverter,
    ITypedConverter<DataObject, TSM.ContourPlate> floorConverter,
    IfcProfileMappingProvider mappingProvider,
    TeklaCatalogValidator catalogValidator,
    ConversionWarningCollector warnings,
    TeklaExistingBeamIndex existingBeamIndex,
    ILogger<IfcFoundationToTeklaConverter> logger
  )
  {
    _lineConverter = lineConverter;
    _pointConverter = pointConverter;
    _floorConverter = floorConverter;
    _mappingProvider = mappingProvider;
    _catalogValidator = catalogValidator;
    _warnings = warnings;
    _existingBeamIndex = existingBeamIndex;
    _logger = logger;
  }

  public TSM.ModelObject Convert(DataObject target) =>
    target["location"] switch
    {
      SOG.Line line => ConvertPadFootingOrPileCap(target, line),
      SOG.Point point => ConvertPile(target, point),
      SOG.Polyline => _floorConverter.Convert(target),
      var other => throw new ConversionException(
        $"IFC-sourced foundation applicationId={target.applicationId} has an unsupported location of type "
          + $"'{other?.GetType().Name ?? "null"}' for Tekla conversion."
      ),
    };

  // Isolated pad footing / pile cap: the enricher already resolved a vertical axis and a clean
  // rectangular "{width}*{depth}" profile from the IFC extrusion - same shape as a point-placed
  // column, so Depth=MIDDLE (centers the section on the axis rather than offsetting it).
  private TSM.Beam ConvertPadFootingOrPileCap(DataObject target, SOG.Line line)
  {
    var seg = _lineConverter.Convert(line);
    string? profile = target["profile"] as string;
    var (resolvedProfile, profileWarning) = _catalogValidator.ValidateOrFallback(
      profile,
      DEFAULT_PILE_PROFILE,
      isProfile: true
    );
    if (profileWarning != null)
    {
      _warnings.Add(target.id, profileWarning);
    }

    if (_existingBeamIndex.TryFindExisting(target.applicationId ?? target.id, out var existingFooting))
    {
      existingFooting!.StartPoint = seg.Point1;
      existingFooting.EndPoint = seg.Point2;
      existingFooting.Profile.ProfileString = resolvedProfile;
      ApplyCommonProperties(existingFooting, target, "Pad Footing", TSM.Position.DepthEnum.MIDDLE);
      existingFooting.Modify();
      StampOrigin(existingFooting, target);
      return existingFooting;
    }

    var beam = new TSM.Beam(seg.Point1, seg.Point2);
    beam.Profile.ProfileString = resolvedProfile;
    ApplyCommonProperties(beam, target, "Pad Footing", TSM.Position.DepthEnum.MIDDLE);
    beam.Insert();
    StampOrigin(beam, target);
    return beam;
  }

  // Pile: no profile/length data exists anywhere for an AdvancedBrep pile body (see
  // RevitNativeSchemaEnricher.TryEnrichPileFromPlacement's remarks) - only the placement anchor
  // point. The ifcTypeName mapping table is the only way to get a correctly-sized pile; absent
  // that, a default vertical segment/profile keeps the receive from silently dropping the element.
  private TSM.Beam ConvertPile(DataObject target, SOG.Point point)
  {
    var basePt = _pointConverter.Convert(point);
    var topPt = new TG.Point(basePt.X, basePt.Y, basePt.Z + DEFAULT_PILE_LENGTH_MM);

    string? ifcTypeName = target["ifcTypeName"] as string;
    string? mappedProfile = null;
    if (ifcTypeName is not null && _mappingProvider.TryGetProfile(ifcTypeName, out var mapped))
    {
      mappedProfile = mapped;
    }
    var (profile, profileWarning) = _catalogValidator.ValidateFirstOrFallback(
      [mappedProfile],
      DEFAULT_PILE_PROFILE,
      isProfile: true
    );
    if (profileWarning != null)
    {
      _warnings.Add(target.id, profileWarning + (ifcTypeName is null ? "" : $" [ifcTypeName: {ifcTypeName}]"));
    }
    _warnings.Add(
      target.id,
      $"Pile has no captured length; used a default {DEFAULT_PILE_LENGTH_MM}mm vertical segment from its "
        + "placement point."
    );

    if (_existingBeamIndex.TryFindExisting(target.applicationId ?? target.id, out var existingPile))
    {
      existingPile!.StartPoint = basePt;
      existingPile.EndPoint = topPt;
      existingPile.Profile.ProfileString = profile;
      ApplyCommonProperties(existingPile, target, "Pile", TSM.Position.DepthEnum.MIDDLE);
      existingPile.Modify();
      StampOrigin(existingPile, target);
      return existingPile;
    }

    var beam = new TSM.Beam(basePt, topPt);
    beam.Profile.ProfileString = profile;
    ApplyCommonProperties(beam, target, "Pile", TSM.Position.DepthEnum.MIDDLE);
    beam.Insert();
    StampOrigin(beam, target);
    return beam;
  }

  private void ApplyCommonProperties(
    TSM.Beam beam,
    DataObject target,
    string fallbackName,
    TSM.Position.DepthEnum depth
  )
  {
    var (material, materialWarning) = _catalogValidator.ValidateOrFallback(null, DEFAULT_MATERIAL, isProfile: false);
    beam.Material.MaterialString = material;
    if (materialWarning != null)
    {
      _warnings.Add(target.id, materialWarning);
    }

    beam.Class = TeklaStandardClasses.FOUNDATION;
    beam.Position.Plane = TSM.Position.PlaneEnum.MIDDLE;
    beam.Position.Rotation = TSM.Position.RotationEnum.TOP;
    beam.Position.Depth = depth;
    beam.Name = target.name.Length > 0 ? IfcNameCleaner.StripTrailingTag(target.name) : fallbackName;
  }

  private void StampOrigin(TSM.Beam beam, DataObject target)
  {
    string? originApplicationId = target.applicationId ?? target.id;
    if (originApplicationId is not null)
    {
      TeklaOriginIdentifier.Set(beam, originApplicationId, _logger);
    }
  }

  public object Convert(object target) => Convert((DataObject)target);
}
