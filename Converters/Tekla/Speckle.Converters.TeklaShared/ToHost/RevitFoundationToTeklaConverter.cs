using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.TeklaShared.Helpers;
using Speckle.Converters.TeklaShared.Helpers.ProfileMapping;
using Speckle.Objects.Data;
using Speckle.Sdk.Common;
using Speckle.Sdk.Common.Exceptions;

namespace Speckle.Converters.TeklaShared.ToHost;

/// <summary>
/// Converts a Revit structural foundation (arriving as a <see cref="RevitObject"/>,
/// builtInCategory OST_StructuralFoundation) into a native Tekla part:
/// point-placed isolated (pad) footings and line-based strip/wall footings become vertical or
/// horizontal concrete <see cref="TSM.Beam"/>s sized from the element's captured displayValue
/// bounding box (the most locale/family-independent dimension source - family parameter names
/// like "Width"/"Breite" are locale-dependent); slab-like foundations (no point/line location)
/// delegate to the Revit floor → <see cref="TSM.ContourPlate"/> converter.
/// </summary>
public class RevitFoundationToTeklaConverter : ITypedConverter<RevitObject, TSM.ModelObject>
{
  private const string DEFAULT_MATERIAL = "C30/37";
  private const double DEFAULT_PAD_SIZE_MM = 1000;
  private const double DEFAULT_THICKNESS_MM = 400;
  private const double DEFAULT_STRIP_WIDTH_MM = 600;

  private readonly ITypedConverter<SOG.Line, TG.LineSegment> _lineConverter;
  private readonly ITypedConverter<SOG.Point, TG.Point> _pointConverter;
  private readonly ITypedConverter<RevitObject, TSM.ContourPlate> _floorConverter;
  private readonly IConverterSettingsStore<TeklaConversionSettings> _settingsStore;
  private readonly RevitProfileMaterialMappingProvider _mappingProvider;
  private readonly TeklaCatalogValidator _catalogValidator;
  private readonly ConversionWarningCollector _warnings;
  private readonly TeklaExistingBeamIndex _existingBeamIndex;
  private readonly ILogger<RevitFoundationToTeklaConverter> _logger;

  public RevitFoundationToTeklaConverter(
    ITypedConverter<SOG.Line, TG.LineSegment> lineConverter,
    ITypedConverter<SOG.Point, TG.Point> pointConverter,
    ITypedConverter<RevitObject, TSM.ContourPlate> floorConverter,
    IConverterSettingsStore<TeklaConversionSettings> settingsStore,
    RevitProfileMaterialMappingProvider mappingProvider,
    TeklaCatalogValidator catalogValidator,
    ConversionWarningCollector warnings,
    TeklaExistingBeamIndex existingBeamIndex,
    ILogger<RevitFoundationToTeklaConverter> logger
  )
  {
    _lineConverter = lineConverter;
    _pointConverter = pointConverter;
    _floorConverter = floorConverter;
    _settingsStore = settingsStore;
    _mappingProvider = mappingProvider;
    _catalogValidator = catalogValidator;
    _warnings = warnings;
    _existingBeamIndex = existingBeamIndex;
    _logger = logger;
  }

  public TSM.ModelObject Convert(RevitObject target) =>
    target["location"] switch
    {
      SOG.Point point => ConvertPadFooting(target, point),
      SOG.Line line => ConvertStripFooting(target, line),
      // Wall/strip foundations must always become beams, even when their run arrives as a
      // polycurve (footing chain under connected walls); open polycurves are runs by definition.
      SOG.Polycurve pc when IsWallOrStripFoundation(target) || !pc.closed => ConvertStripFootingRun(target, pc),
      // Revit WallFoundations expose NO location at all (their Location has no curve) - derive
      // the run from the captured geometry instead of failing or misrouting to the slab path.
      null when IsWallOrStripFoundation(target) => ConvertStripFootingFromGeometry(target),
      // Slab-like foundations (Bodenplatte/Fundamentplatte) carry a closed floor-style boundary
      // polycurve (or no location at all) - reuse the floor → ContourPlate slab conversion.
      SOG.Polycurve or null => _floorConverter.Convert(target),
      var other => throw new ConversionException(
        $"Revit foundation '{target.family} : {target.type}' (name '{target.name}') has an unsupported "
          + $"location of type '{other.GetType().Name}' for Tekla conversion."
      ),
    };

  // Wall-foundation detection by family/type/name tokens (DE + EN); locale-dependent by nature,
  // but a wrong guess only changes beam-vs-slab representation, never fails the element.
  private static readonly string[] s_wallFoundationTokens =
  [
    "wandfundament",
    "streifenfundament",
    "wall foundation",
    "strip foot",
    "strip found",
    "continuous foot",
  ];

  private static bool IsWallOrStripFoundation(RevitObject target)
  {
    string haystack = $"{target.family} {target.type} {target.name}".ToLowerInvariant();
    return s_wallFoundationTokens.Any(haystack.Contains);
  }

  /// <summary>
  /// Converts a multi-segment strip-footing run into one straight Tekla beam per segment
  /// (arc segments become their chords, with a warning). Returns the first created beam.
  ///
  /// Unlike the single-beam footing cases, this doesn't attempt segment-by-segment reuse: every
  /// segment is stamped with the SAME origin applicationId (harmless - only the single-beam lookup
  /// path ever re-reads that key) but never looked up first, so a re-send always inserts a fresh set
  /// of beams. Any previously-created segments for this same run are left unclaimed this receive and
  /// get swept up by the existing "removed at source" deletion pass instead - a simpler and safer
  /// policy than trying to align a run whose segment count may itself have changed at the source.
  /// </summary>
  private TSM.Beam ConvertStripFootingRun(RevitObject target, SOG.Polycurve polycurve)
  {
    double scale = RevitPropertyReader.GetUnitScaleFactor(target.units, _settingsStore.Current.SpeckleUnits);
    var (thicknessMm, widthMm) = GetStripCrossSectionMm(target, runDirection: null);

    TSM.Beam? first = null;
    foreach (var segment in polycurve.segments)
    {
      SOG.Point segStart;
      SOG.Point segEnd;
      switch (segment)
      {
        case SOG.Line l:
          segStart = l.start;
          segEnd = l.end;
          break;
        case SOG.Arc a:
          segStart = a.startPoint;
          segEnd = a.endPoint;
          _warnings.Add(target.id, "Curved strip-footing segment approximated by its straight chord.");
          break;
        default:
          _warnings.Add(target.id, $"Skipped unsupported strip-footing segment '{segment.GetType().Name}'.");
          continue;
      }

      var beam = new TSM.Beam(
        _pointConverter.Convert(RevitPropertyReader.ScalePoint(segStart, scale)),
        _pointConverter.Convert(RevitPropertyReader.ScalePoint(segEnd, scale))
      );
      beam.Profile.ProfileString = $"{thicknessMm:0.#}*{widthMm:0.#}";
      ApplyCommonProperties(beam, target, "Strip Footing");
      beam.Insert();
      StampOrigin(beam, target);
      first ??= beam;
    }

    return first
      ?? throw new ConversionException(
        $"Strip-footing run '{target.name}' contained no convertible segments ({polycurve.segments.Count} segments)."
      );
  }

  private TSM.Beam ConvertPadFooting(RevitObject target, SOG.Point locationPoint)
  {
    double mmToModel = RevitPropertyReader.GetUnitScaleFactor(Units.Millimeters, _settingsStore.Current.SpeckleUnits);
    string units = _settingsStore.Current.SpeckleUnits;

    SOG.Point topPoint;
    SOG.Point bottomPoint;
    double sizeXMm;
    double sizeYMm;

    if (RevitPropertyReader.TryGetDisplayValueBBoxMm(target, out var bbox) && bbox.SizeZ > 1e-3)
    {
      // Real captured geometry: plan center + top/bottom faces directly from the mesh bounds.
      topPoint = new SOG.Point(bbox.CenterX * mmToModel, bbox.CenterY * mmToModel, bbox.MaxZ * mmToModel, units);
      bottomPoint = new SOG.Point(bbox.CenterX * mmToModel, bbox.CenterY * mmToModel, bbox.MinZ * mmToModel, units);
      sizeXMm = bbox.SizeX;
      sizeYMm = bbox.SizeY;
      _warnings.Add(
        target.id,
        "Pad footing dimensions derived from the element's bounding box - verify size/orientation "
          + "(rotated footings get their world-axis-aligned extents)."
      );
    }
    else
    {
      // No usable mesh: footing extends downward from its placement point with default dimensions.
      double scale = RevitPropertyReader.GetUnitScaleFactor(target.units, _settingsStore.Current.SpeckleUnits);
      var scaled = RevitPropertyReader.ScalePoint(locationPoint, scale);
      topPoint = new SOG.Point(scaled.x, scaled.y, scaled.z, units);
      bottomPoint = new SOG.Point(scaled.x, scaled.y, scaled.z - (DEFAULT_THICKNESS_MM * mmToModel), units);
      sizeXMm = DEFAULT_PAD_SIZE_MM;
      sizeYMm = DEFAULT_PAD_SIZE_MM;
      _warnings.Add(
        target.id,
        $"Pad footing has no captured geometry; used default size {DEFAULT_PAD_SIZE_MM}*{DEFAULT_PAD_SIZE_MM}*{DEFAULT_THICKNESS_MM}mm."
      );
    }

    TG.Point start = _pointConverter.Convert(topPoint);
    TG.Point end = _pointConverter.Convert(bottomPoint);
    string profile = $"{sizeYMm:0.#}*{sizeXMm:0.#}";

    if (_existingBeamIndex.TryFindExisting(target.applicationId ?? target.id, out var existingFooting))
    {
      existingFooting!.StartPoint = start;
      existingFooting.EndPoint = end;
      existingFooting.Profile.ProfileString = profile;
      // Unlike strip/wall footings (placed at the wall's location line, an edge reference), a pad
      // footing's beam runs through the bbox centroid - Depth=BEHIND would shift the profile off-axis
      // by half its depth, same issue point-placed columns have (see RevitColumnBeamToTeklaBeamConverter).
      ApplyCommonProperties(existingFooting, target, "Pad Footing", TSM.Position.DepthEnum.MIDDLE);
      existingFooting.Modify();
      StampOrigin(existingFooting, target);
      return existingFooting;
    }

    var beam = new TSM.Beam(start, end);
    beam.Profile.ProfileString = profile;
    ApplyCommonProperties(beam, target, "Pad Footing", TSM.Position.DepthEnum.MIDDLE);
    beam.Insert();
    StampOrigin(beam, target);
    return beam;
  }

  /// <summary>
  /// Wall footing without any location (Revit WallFoundation's Location carries no curve): derive
  /// a straight run along the captured geometry's dominant plan axis, at the footing's top face.
  /// Exact for axis-aligned footings; rotated ones get flagged for review.
  /// </summary>
  private TSM.Beam ConvertStripFootingFromGeometry(RevitObject target)
  {
    if (!RevitPropertyReader.TryGetDisplayValueBBoxMm(target, out var bbox) || bbox.SizeZ <= 1e-3)
    {
      throw new ConversionException(
        $"Wall footing '{target.family} : {target.type}' (name '{target.name}') has neither a location nor "
          + "captured geometry - cannot determine its run."
      );
    }

    bool runAlongX = bbox.SizeX >= bbox.SizeY;
    // Tekla model coordinates are always millimeters, so the mm bounding box maps directly.
    var start = runAlongX
      ? new TG.Point(bbox.MinX, bbox.CenterY, bbox.MaxZ)
      : new TG.Point(bbox.CenterX, bbox.MinY, bbox.MaxZ);
    var end = runAlongX
      ? new TG.Point(bbox.MaxX, bbox.CenterY, bbox.MaxZ)
      : new TG.Point(bbox.CenterX, bbox.MaxY, bbox.MaxZ);

    var (thicknessMm, widthMm) = GetStripCrossSectionMm(target, runAlongX ? (1.0, 0.0) : (0.0, 1.0));

    _warnings.Add(
      target.id,
      "Wall footing run derived from its bounding box (Revit provides no location line) - verify "
        + "direction/extent for rotated or L-shaped footings."
    );

    string profile = $"{thicknessMm:0.#}*{widthMm:0.#}";
    return CreateOrUpdateStripFooting(target, start, end, profile);
  }

  private TSM.Beam ConvertStripFooting(RevitObject target, SOG.Line line)
  {
    double scale = RevitPropertyReader.GetUnitScaleFactor(target.units, _settingsStore.Current.SpeckleUnits);
    var segment = _lineConverter.Convert(RevitPropertyReader.ScaleLine(line, scale));

    double runDx = Math.Abs(line.end.x - line.start.x);
    double runDy = Math.Abs(line.end.y - line.start.y);
    var (thicknessMm, widthMm) = GetStripCrossSectionMm(target, (runDx, runDy));

    string profile = $"{thicknessMm:0.#}*{widthMm:0.#}";
    // Revit places a wall foundation's location line at the wall base (= top of footing), so the
    // profile extrudes downward from the axis (Depth=BEHIND, the default applied inside).
    return CreateOrUpdateStripFooting(target, segment.Point1, segment.Point2, profile);
  }

  private TSM.Beam CreateOrUpdateStripFooting(RevitObject target, TG.Point start, TG.Point end, string profile)
  {
    if (_existingBeamIndex.TryFindExisting(target.applicationId ?? target.id, out var existingFooting))
    {
      existingFooting!.StartPoint = start;
      existingFooting.EndPoint = end;
      existingFooting.Profile.ProfileString = profile;
      ApplyCommonProperties(existingFooting, target, "Strip Footing");
      existingFooting.Modify();
      StampOrigin(existingFooting, target);
      return existingFooting;
    }

    var beam = new TSM.Beam(start, end);
    beam.Profile.ProfileString = profile;
    ApplyCommonProperties(beam, target, "Strip Footing");
    beam.Insert();
    StampOrigin(beam, target);
    return beam;
  }

  // Wall-foundation width parameter names (locale variants) - Revit wall foundations expose their
  // total width; the built-in thickness parameter name is locale-independent.
  private static readonly string[] s_stripWidthParamNames = ["Breite", "Width", "b"];
  private static readonly string[] s_thicknessParamNames = ["FOUNDATION_THICKNESS", "Dicke", "Thickness"];

  /// <summary>
  /// Strip-footing cross-section, per dimension trying: the Revit parameter, dimensions parsed
  /// from the type name (e.g. "STB 1000 x 500" = width 1000, thickness 500), the geometry's
  /// extents (width only when a run direction makes the perpendicular extent meaningful),
  /// then the default.
  /// </summary>
  private (double ThicknessMm, double WidthMm) GetStripCrossSectionMm(
    RevitObject target,
    (double Dx, double Dy)? runDirection
  )
  {
    bool hasBBox = RevitPropertyReader.TryGetDisplayValueBBoxMm(target, out var bbox) && bbox.SizeZ > 1e-3;
    bool hasNameDims =
      RevitPropertyReader.TryParseSectionFromName(target.type, out double nameWidthMm, out double nameThicknessMm)
      || RevitPropertyReader.TryParseSectionFromName(target.name, out nameWidthMm, out nameThicknessMm);

    double thicknessMm;
    if (RevitPropertyReader.TryGetLengthParamMm(target, s_thicknessParamNames, out double paramThickness))
    {
      thicknessMm = paramThickness;
    }
    else if (hasNameDims)
    {
      thicknessMm = nameThicknessMm;
    }
    else if (hasBBox)
    {
      thicknessMm = bbox.SizeZ;
    }
    else
    {
      thicknessMm = DEFAULT_THICKNESS_MM;
      _warnings.Add(target.id, $"Strip footing thickness not captured; used default {DEFAULT_THICKNESS_MM}mm.");
    }

    double widthMm;
    if (RevitPropertyReader.TryGetLengthParamMm(target, s_stripWidthParamNames, out double paramWidth))
    {
      widthMm = paramWidth;
    }
    else if (hasNameDims)
    {
      widthMm = nameWidthMm;
    }
    else if (hasBBox && runDirection is { } dir)
    {
      // Bbox extent perpendicular to the run - meaningless for multi-segment (L/U-shaped) runs,
      // where the bbox spans all legs.
      widthMm = dir.Dx >= dir.Dy ? bbox.SizeY : bbox.SizeX;
      _warnings.Add(
        target.id,
        "Strip footing width derived from the element's bounding box - verify for non-axis-aligned runs."
      );
    }
    else
    {
      widthMm = DEFAULT_STRIP_WIDTH_MM;
      _warnings.Add(target.id, $"Strip footing width not captured; used default {DEFAULT_STRIP_WIDTH_MM}mm.");
    }

    return (thicknessMm, widthMm);
  }

  private void ApplyCommonProperties(
    TSM.Beam beam,
    RevitObject target,
    string fallbackName,
    TSM.Position.DepthEnum depth = TSM.Position.DepthEnum.BEHIND
  )
  {
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
    beam.Material.MaterialString = material;
    if (materialWarning != null)
    {
      _warnings.Add(target.id, materialWarning);
    }

    beam.Class = TeklaStandardClasses.FOUNDATION;
    beam.Position.Plane = TSM.Position.PlaneEnum.MIDDLE;
    beam.Position.Rotation = TSM.Position.RotationEnum.TOP;
    beam.Position.Depth = depth;
    beam.Name = target.name.Length > 0 ? target.name : fallbackName;
  }

  private void StampOrigin(TSM.Beam beam, RevitObject target)
  {
    string? originApplicationId = target.applicationId ?? target.id;
    if (originApplicationId is not null)
    {
      TeklaOriginIdentifier.Set(beam, originApplicationId, _logger);
    }
  }

  public object Convert(object target) => Convert((RevitObject)target);
}
