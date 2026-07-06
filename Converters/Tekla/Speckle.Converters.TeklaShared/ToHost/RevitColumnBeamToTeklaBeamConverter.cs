using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.TeklaShared.Helpers;
using Speckle.Converters.TeklaShared.Helpers.ProfileMapping;
using Speckle.Objects.Data;
using Speckle.Sdk.Common;
using Speckle.Sdk.Common.Exceptions;

namespace Speckle.Converters.TeklaShared.ToHost;

/// <summary>
/// Converts a Revit structural Column or Beam (arriving as a <see cref="RevitObject"/>) into a
/// Tekla part. Straight (line-placed) elements become a <see cref="TSM.Beam"/>; point-placed
/// columns synthesize a vertical segment using the column's height; curved axes (arc, polyline,
/// polycurve) become a <see cref="TSM.PolyBeam"/> following the true curve (arcs via
/// CHAMFER_ARC_POINT contour points - exact, not a chord approximation).
/// </summary>
public class RevitColumnBeamToTeklaBeamConverter : ITypedConverter<RevitObject, TSM.Part>
{
  private const string DEFAULT_PROFILE = "HEA200";
  private const string DEFAULT_MATERIAL = "S235JR";
  private const double DEFAULT_COLUMN_HEIGHT_MM = 3000;

  private static readonly string[] ProfilePrefixes = ["HE", "IPE", "UC", "UB", "RHS", "CHS", "SHS", "UPE", "UPN", "L"];

  private readonly ITypedConverter<SOG.Line, TG.LineSegment> _lineConverter;
  private readonly ITypedConverter<SOG.Point, TG.Point> _pointConverter;
  private readonly IConverterSettingsStore<TeklaConversionSettings> _settingsStore;
  private readonly RevitProfileMaterialMappingProvider _mappingProvider;
  private readonly TeklaCatalogValidator _catalogValidator;
  private readonly ConversionWarningCollector _warnings;
  private readonly TeklaExistingBeamIndex _existingBeamIndex;
  private readonly ILogger<RevitColumnBeamToTeklaBeamConverter> _logger;

  public RevitColumnBeamToTeklaBeamConverter(
    ITypedConverter<SOG.Line, TG.LineSegment> lineConverter,
    ITypedConverter<SOG.Point, TG.Point> pointConverter,
    IConverterSettingsStore<TeklaConversionSettings> settingsStore,
    RevitProfileMaterialMappingProvider mappingProvider,
    TeklaCatalogValidator catalogValidator,
    ConversionWarningCollector warnings,
    TeklaExistingBeamIndex existingBeamIndex,
    ILogger<RevitColumnBeamToTeklaBeamConverter> logger
  )
  {
    _lineConverter = lineConverter;
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
    double scale = RevitPropertyReader.GetUnitScaleFactor(target.units, _settingsStore.Current.SpeckleUnits);

    TSM.Part part;
    bool isPointPlacedColumn = false;
    bool isUpdate = false;
    double rotationDegrees = 0;
    switch (target["location"])
    {
      case SOG.Line line:
      {
        var seg = _lineConverter.Convert(RevitPropertyReader.ScaleLine(line, scale));

        _logger.LogInformation(
          "RevitColumnBeamToTeklaBeamConverter: builtInCategory={BuiltInCategory} applicationId={ApplicationId} id={Id}",
          target["builtInCategory"] as string,
          target.applicationId,
          target.id
        );

        // Beams/Braces and line-placed Columns whose origin applicationId matches an already-received
        // part in this model are updates, not new elements - reuse and reposition the existing part
        // instead of inserting a duplicate. This is what lets a Tekla-authored beam survive a Revit
        // round-trip (edit + resend) without cloning itself in the Tekla model.
        string? lineBuiltInCategory = target["builtInCategory"] as string;
        if (
          (lineBuiltInCategory == "OST_StructuralFraming" || lineBuiltInCategory == "OST_StructuralColumns")
          && _existingBeamIndex.TryFindExisting(target.applicationId ?? target.id, out var existingBeam)
        )
        {
          existingBeam!.StartPoint = seg.Point1;
          existingBeam.EndPoint = seg.Point2;
          part = existingBeam;
          isUpdate = true;
        }
        else
        {
          part = CreateStraightBeam(target, seg.Point1, seg.Point2);
        }
        break;
      }
      // Curved (arc) axis: a Tekla PolyBeam models a true circular arc as three contour points
      // with the middle one marked CHAMFER_ARC_POINT - exact, no chord approximation.
      case SOG.Arc arc:
        part = CreateArcPolyBeam(
          _pointConverter.Convert(RevitPropertyReader.ScalePoint(arc.startPoint, scale)),
          _pointConverter.Convert(RevitPropertyReader.ScalePoint(arc.midPoint, scale)),
          _pointConverter.Convert(RevitPropertyReader.ScalePoint(arc.endPoint, scale))
        );
        break;
      // Multi-vertex sketched framing: PolyBeam through every vertex - exact.
      case SOG.Polyline polyline when polyline.GetPoints() is { Count: >= 2 } pts:
      {
        var polyBeam = new TSM.PolyBeam();
        foreach (SOG.Point p in pts)
        {
          polyBeam.AddContourPoint(
            new TSM.ContourPoint(_pointConverter.Convert(RevitPropertyReader.ScalePoint(p, scale)), new TSM.Chamfer())
          );
        }
        part = polyBeam;
        break;
      }
      // Mixed line/arc axes: PolyBeam with narrow arcs as rounded virtual corners (exact) and
      // wide arcs tessellated - same construction the plate/opening contours use.
      case SOG.Polycurve polycurve when polycurve.segments.Count > 0:
        part = CreatePolycurvePolyBeam(polycurve, scale);
        break;
      case SOG.Point point when target["builtInCategory"] as string == "OST_StructuralColumns":
      {
        // Column placed by point: vertical segment through the placement point, spanning the
        // captured geometry's real Z-extent (exact - reflects base/top offsets); parameter/default
        // height fallback if no geometry.
        isPointPlacedColumn = true;
        var (start, end) = SynthesizeVerticalSegment(target, RevitPropertyReader.ScalePoint(point, scale));

        // Mirrors the line-placed case above: a point-placed column round-tripping from Revit
        // reuses/repositions its existing Tekla part instead of inserting a duplicate.
        if (_existingBeamIndex.TryFindExisting(target.applicationId ?? target.id, out var existingColumn))
        {
          existingColumn!.StartPoint = start;
          existingColumn.EndPoint = end;
          part = existingColumn;
          isUpdate = true;
        }
        else
        {
          part = CreateStraightBeam(target, start, end);
        }

        // Plan rotation about the vertical axis, captured from Revit's LocationPoint.Rotation
        // (radians). Sign flipped: Revit and Tekla rotate in opposite directions (verified in test).
        if (point["rotation"] is double rotationRadians)
        {
          rotationDegrees = -(rotationRadians * 180.0 / Math.PI);
        }
        break;
      }
      default:
        throw new ConversionException(
          $"Revit {target.category} '{target.family} : {target.type}' (name '{target.name}') has an unsupported "
            + $"location of type '{target.location?.GetType().Name ?? "null"}' - Tekla Beam conversion needs a "
            + "line/arc/polyline location (or a point, for columns)."
        );
    }

    var (profile, profileWarning) = ResolveProfile(target);
    part.Profile.ProfileString = profile;
    if (profileWarning != null)
    {
      _warnings.Add(target.id, profileWarning);
    }

    var (material, materialWarning) = ResolveMaterial(target);
    part.Material.MaterialString = material;
    if (materialWarning != null)
    {
      _warnings.Add(target.id, materialWarning);
    }

    // Standard class (color) assignment: steel/concrete beams and columns each get their own class.
    bool isColumn = target["builtInCategory"] as string == "OST_StructuralColumns";
    part.Class = isColumn ? TeklaStandardClasses.ForColumn(material) : TeklaStandardClasses.ForBeam(material);

    part.Position.Plane = TSM.Position.PlaneEnum.MIDDLE;
    part.Position.Rotation = TSM.Position.RotationEnum.TOP;
    if (isPointPlacedColumn)
    {
      // Revit's column placement point is the section centroid - center the profile on the axis
      // in both directions (Depth=BEHIND would shift the section off-axis by half its depth).
      part.Position.Depth = TSM.Position.DepthEnum.MIDDLE;
    }
    else
    {
      // Matches Tekla's "Betonträger" beam defaults: baseline as beam axis, section below it
      // (Revit framing's default z-justification is Top, i.e. location line at the top face).
      part.Position.Depth = TSM.Position.DepthEnum.BEHIND;
      // Revit framing "Cross-Section Rotation" (Querschnittsdrehung) instance parameter.
      // Sign flipped: Revit and Tekla rotate in opposite directions (verified in test).
      if (
        RevitPropertyReader.TryGetParameter(target, "Instance Parameters", "STRUCTURAL_BEND_DIR_ANGLE", out var angle)
        && RevitPropertyReader.TryToDouble(angle!.GetOrDefault("value"), out var angleValue)
      )
      {
        rotationDegrees = -RevitPropertyReader.ConvertToDegrees(
          angleValue,
          angle.GetOrDefault("unitsTypeId") as string
        );
      }
    }
    if (Math.Abs(rotationDegrees) > 1e-6)
    {
      part.Position.RotationOffset = rotationDegrees;
    }
    part.Name = target.name.Length > 0 ? target.name : target.type;

    string? originApplicationId = target.applicationId ?? target.id;
    if (isUpdate)
    {
      part.Modify();
      // Stamp the UDA even on a native-GUID-matched update (a beam originally authored in Tekla,
      // never before "received") so this beam becomes deletion-tracked from now on: once Speckle has
      // touched it at least once, a future receive that no longer includes it can recognize it as
      // removed at the source. Beams that have never round-tripped are never touched here, so
      // unrelated native Tekla content can never become an unintended deletion candidate.
      if (originApplicationId is not null)
      {
        TeklaOriginIdentifier.Set(part, originApplicationId, _logger);
      }
    }
    else
    {
      part.Insert();
      if (originApplicationId is not null)
      {
        TeklaOriginIdentifier.Set(part, originApplicationId, _logger);
      }
    }
    return part;
  }

  private static TSM.Beam CreateStraightBeam(RevitObject target, TG.Point start, TG.Point end)
  {
    double dx = end.X - start.X;
    double dy = end.Y - start.Y;
    double dz = end.Z - start.Z;
    if (dx * dx + dy * dy + dz * dz < 1e-6)
    {
      throw new ConversionException(
        $"Revit {target.category} '{target.name}' location resolves to a degenerate (zero-length) segment."
      );
    }
    return new TSM.Beam(start, end);
  }

  internal static TSM.PolyBeam CreateArcPolyBeam(TG.Point start, TG.Point arcMid, TG.Point end)
  {
    var polyBeam = new TSM.PolyBeam();
    polyBeam.AddContourPoint(new TSM.ContourPoint(start, new TSM.Chamfer()));
    polyBeam.AddContourPoint(
      new TSM.ContourPoint(arcMid, new TSM.Chamfer(0, 0, TSM.Chamfer.ChamferTypeEnum.CHAMFER_ARC_POINT))
    );
    polyBeam.AddContourPoint(new TSM.ContourPoint(end, new TSM.Chamfer()));
    return polyBeam;
  }

  private TSM.PolyBeam CreatePolycurvePolyBeam(SOG.Polycurve polycurve, double scale)
  {
    var polyBeam = new TSM.PolyBeam();

    // PolycurveToScaledPoints emits each segment's leading vertex only (closed-contour semantics,
    // with arcs as roundable virtual corners) - an open polybeam additionally needs the run's true
    // start when the first segment is an arc (its leading vertex is the virtual corner, not the
    // arc start) and always needs the last segment's end point.
    if (polycurve.segments[0] is SOG.Arc firstArc)
    {
      polyBeam.AddContourPoint(
        new TSM.ContourPoint(
          _pointConverter.Convert(RevitPropertyReader.ScalePoint(firstArc.startPoint, scale)),
          new TSM.Chamfer()
        )
      );
    }

    foreach (var (point, chamferRadius) in RevitPropertyReader.PolycurveToScaledPoints(polycurve, scale))
    {
      var chamfer =
        chamferRadius > 0
          ? new TSM.Chamfer(chamferRadius, chamferRadius, TSM.Chamfer.ChamferTypeEnum.CHAMFER_ROUNDING)
          : new TSM.Chamfer();
      polyBeam.AddContourPoint(new TSM.ContourPoint(_pointConverter.Convert(point), chamfer));
    }

    if (!polycurve.closed && GetSegmentEndpoints(polycurve.segments[^1]).End is { } lastEnd)
    {
      polyBeam.AddContourPoint(
        new TSM.ContourPoint(
          _pointConverter.Convert(RevitPropertyReader.ScalePoint(lastEnd, scale)),
          new TSM.Chamfer()
        )
      );
    }

    return polyBeam;
  }

  // Endpoint extraction for polycurve segments that are lines or arcs (the shapes Revit location
  // curves decompose into).
  private static (SOG.Point? Start, SOG.Point? End) GetSegmentEndpoints(Speckle.Objects.ICurve segment) =>
    segment switch
    {
      SOG.Line l => (l.start, l.end),
      SOG.Arc a => (a.startPoint, a.endPoint),
      _ => (null, null),
    };

  /// <summary>
  /// Resolves the Tekla profile string for <paramref name="target"/>, trying in order: mapping
  /// table entry, name/parameter heuristic (also with whitespace stripped - Revit type names like
  /// "HEA 300" contain spaces that Tekla catalog names don't), and finally a section synthesized
  /// from the captured geometry's bounding box. First candidate present in the Tekla catalog wins;
  /// otherwise <see cref="DEFAULT_PROFILE"/> with a warning naming every candidate tried.
  /// </summary>
  private (string Value, string? Warning) ResolveProfile(RevitObject target)
  {
    var candidates = new List<string?>();
    if (_mappingProvider.TryGetProfile(target.family, target.type, out var mapped))
    {
      candidates.Add(mapped);
    }

    string? heuristic = TryMapProfileHeuristic(target);
    candidates.Add(heuristic);
    if (heuristic != null && heuristic.IndexOf(' ') >= 0)
    {
      candidates.Add(heuristic.Replace(" ", ""));
    }

    // Rectangular section encoded in the type name ("STB 30/30", "Stütze 300 x 450") - common
    // German naming convention, and often the only dimension source for instanced families whose
    // per-element geometry and parameters aren't captured.
    if (
      RevitPropertyReader.TryParseSectionFromName(target.type, out double nameWidthMm, out double nameHeightMm)
      || RevitPropertyReader.TryParseSectionFromName(target.name, out nameWidthMm, out nameHeightMm)
    )
    {
      candidates.Add($"{nameHeightMm:0.#}*{nameWidthMm:0.#}");
    }

    string? bboxProfile = TryGetBBoxSectionMm(target, out double bboxWidthMm, out double bboxHeightMm)
      ? $"{bboxHeightMm:0.#}*{bboxWidthMm:0.#}"
      : null;
    candidates.Add(bboxProfile);

    var result = _catalogValidator.ValidateFirstOrFallback(candidates, DEFAULT_PROFILE, isProfile: true);
    if (bboxProfile != null && result.Warning == null && result.Value == bboxProfile)
    {
      _warnings.Add(
        target.id,
        $"Profile '{bboxProfile}' derived from the element's bounding box (no catalog/parameter match) - verify."
      );
    }
    if (result.Warning != null)
    {
      // Nothing resolved: list what parameters WERE captured, so the missing dimension source is
      // diagnosable from the receive report alone.
      var typeParams = RevitPropertyReader.GetCapturedParameterNames(target, "Type Parameters");
      var instParams = RevitPropertyReader.GetCapturedParameterNames(target, "Instance Parameters");
      result = (
        result.Value,
        result.Warning
          + $" [captured type params: {string.Join(", ", typeParams.Take(25))}]"
          + $" [captured instance params: {string.Join(", ", instParams.Take(25))}]"
      );
    }
    return result;
  }

  /// <summary>
  /// Resolves the Tekla material string for <paramref name="target"/>, trying in order: mapping
  /// table entry keyed by the captured Revit structural material name, then that raw Revit name
  /// itself (it may directly be a Tekla material or alias, e.g. "C30/37" or "S235"). Falls back to
  /// <see cref="DEFAULT_MATERIAL"/> with a warning naming the Revit material, so the user knows
  /// exactly which key to add to the mapping table.
  /// </summary>
  private (string Value, string? Warning) ResolveMaterial(RevitObject target)
  {
    var candidates = new List<string?>();
    if (RevitPropertyReader.TryGetStructuralMaterialName(target, out var revitMaterialName))
    {
      if (_mappingProvider.TryGetMaterial(revitMaterialName!, out var mapped))
      {
        candidates.Add(mapped);
      }
      candidates.Add(revitMaterialName);
    }

    return _catalogValidator.ValidateFirstOrFallback(candidates, DEFAULT_MATERIAL, isProfile: false);
  }

  /// <summary>
  /// Last-resort cross-section from the captured geometry's world-axis bounding box, oriented by
  /// the element's run direction: vertical runs (columns) use the plan extents; horizontal runs
  /// use the Z-extent as height and the horizontal extent perpendicular to the dominant run axis
  /// as width. Approximate for diagonal-in-plan runs.
  /// </summary>
  private bool TryGetBBoxSectionMm(RevitObject target, out double widthMm, out double heightMm)
  {
    widthMm = 0;
    heightMm = 0;
    if (!RevitPropertyReader.TryGetDisplayValueBBoxMm(target, out var bbox))
    {
      return false;
    }

    double runDx = 0,
      runDy = 0,
      runDz = 1;
    if (target["location"] is SOG.Line line)
    {
      runDx = Math.Abs(line.end.x - line.start.x);
      runDy = Math.Abs(line.end.y - line.start.y);
      runDz = Math.Abs(line.end.z - line.start.z);
    }

    if (runDz >= runDx && runDz >= runDy)
    {
      // Vertical run (column): section lies in plan.
      widthMm = bbox.SizeX;
      heightMm = bbox.SizeY;
    }
    else
    {
      heightMm = bbox.SizeZ;
      widthMm = runDx >= runDy ? bbox.SizeY : bbox.SizeX;
    }

    return widthMm > 1 && heightMm > 1;
  }

  // Best-effort: if the Revit type name looks like a standard profile designation
  // (e.g. "HEA200", "IPE300", "UC 305x305x137"), pass it through verbatim - Tekla's
  // ProfileString parser accepts many common European/AISC designations directly.
  // Otherwise try the "d" type parameter that Revit's round concrete column/beam family templates
  // ("Concrete-Round-Column"/"-Beam") expose for their diameter, building a Tekla round profile
  // "D{diameter}". Otherwise try the "b"/"h" type parameters that Revit's standard rectangular
  // concrete column/beam family templates ("Concrete-Rectangular-Column"/"-Beam") expose for their
  // cross-section, building a bare "{height}*{width}" rectangular profile. Returns null (caller
  // falls back to DEFAULT_PROFILE) if none is available.
  // internal (not private) so RevitColumnBeamToTeklaBeamConverterProfileHeuristicTests can exercise
  // these pure heuristics directly, without constructing the full converter's DI dependencies.
  internal static string? TryMapProfileHeuristic(RevitObject target)
  {
    if (target.type.Length > 0 && LooksLikeProfileDesignation(target.type))
    {
      return target.type;
    }

    if (TryGetRoundProfileMm(target, out double diameterMm))
    {
      return $"D{diameterMm:0.#}";
    }

    if (TryGetRectangularProfileMm(target, out double widthMm, out double heightMm))
    {
      return $"{heightMm:0.#}*{widthMm:0.#}";
    }

    return null;
  }

  internal static bool LooksLikeProfileDesignation(string typeName) =>
    ProfilePrefixes.Any(prefix => typeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

  // Family dimension parameter names are family-author-defined and locale-dependent - try the
  // common English template names and their German equivalents, in both parameter buckets.
  private static readonly string[] s_diameterParamNames = ["d", "D", "Durchmesser", "Diameter"];
  private static readonly string[] s_widthParamNames = ["b", "Breite", "Width"];
  private static readonly string[] s_heightParamNames = ["h", "Höhe", "Hoehe", "Height"];

  internal static bool TryGetRoundProfileMm(RevitObject target, out double diameterMm) =>
    RevitPropertyReader.TryGetLengthParamMm(target, s_diameterParamNames, out diameterMm);

  internal static bool TryGetRectangularProfileMm(RevitObject target, out double widthMm, out double heightMm)
  {
    bool hasWidth = RevitPropertyReader.TryGetLengthParamMm(target, s_widthParamNames, out widthMm);
    bool hasHeight = RevitPropertyReader.TryGetLengthParamMm(target, s_heightParamNames, out heightMm);
    return hasWidth && hasHeight;
  }

  private static readonly string[] s_columnLengthParamNames = ["INSTANCE_LENGTH_PARAM", "Länge", "Length"];

  private (TG.Point, TG.Point) SynthesizeVerticalSegment(RevitObject target, SOG.Point scaledBasePoint)
  {
    var basePt = _pointConverter.Convert(scaledBasePoint);

    // Prefer the captured geometry's real Z-range: exact base and top elevation regardless of
    // level constraints and base/top offsets. Tekla model coordinates are always millimeters,
    // so the mm bounding box maps directly onto TG.Point Z.
    if (RevitPropertyReader.TryGetDisplayValueBBoxMm(target, out var bbox) && bbox.SizeZ > 1e-3)
    {
      return (new TG.Point(basePt.X, basePt.Y, bbox.MinZ), new TG.Point(basePt.X, basePt.Y, bbox.MaxZ));
    }

    // No geometry: Revit structural columns expose their actual length as the built-in "Length"
    // (Länge) instance parameter - always positive, unlike the top-level *offset* (which is 0 for
    // level-flush tops and can be negative, and must never be used as a height). Base sits at the
    // placement point plus the base-level offset; column extends upward by its length.
    if (RevitPropertyReader.TryGetLengthParamMm(target, s_columnLengthParamNames, out double lengthMm))
    {
      double baseOffsetMm = 0;
      if (
        RevitPropertyReader.TryGetParameter(target, "Instance Parameters", "FAMILY_BASE_LEVEL_OFFSET_PARAM", out var p)
        && RevitPropertyReader.TryToDouble(p!.GetOrDefault("value"), out var offsetValue)
      )
      {
        baseOffsetMm = RevitPropertyReader.ConvertToMm(offsetValue, p.GetOrDefault("unitsTypeId") as string);
      }

      double baseZ = basePt.Z + baseOffsetMm;
      return (new TG.Point(basePt.X, basePt.Y, baseZ), new TG.Point(basePt.X, basePt.Y, baseZ + lengthMm));
    }

    var instParams = RevitPropertyReader.GetCapturedParameterNames(target, "Instance Parameters");
    _warnings.Add(
      target.id,
      $"Column has no captured geometry or length parameter; used default height {DEFAULT_COLUMN_HEIGHT_MM}mm "
        + "upward from the placement point."
        + $" [captured instance params: {string.Join(", ", instParams.Take(25))}]"
    );
    return (basePt, new TG.Point(basePt.X, basePt.Y, basePt.Z + DEFAULT_COLUMN_HEIGHT_MM));
  }

  public object Convert(object target) => Convert((RevitObject)target);
}
