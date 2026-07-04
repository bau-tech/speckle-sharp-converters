using Speckle.Objects;
using Speckle.Objects.Data;
using Speckle.Sdk.Common;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.TeklaShared.Helpers;

/// <summary>
/// Minimal, Tekla-side-only port of the Revit-side RevitElementPropertyApplicator's parameter
/// lookup logic (TryGetParameter/TryToDouble), plus helpers for scaling RevitObject-sourced
/// geometry (captured in Revit's document units) into the Tekla model's units.
/// </summary>
public static class RevitPropertyReader
{
  // System.Collections.Generic.CollectionExtensions.GetValueOrDefault is netstandard2.1+/netcoreapp2.0+
  // only and unavailable on this project's net48 target. Parameter is nullable-valued so this
  // also accepts non-nullable Dictionary<string, object> receivers (e.g. Base's own property bag,
  // which is Dictionary<string, object?>) without a CS8620 nullability mismatch at call sites.
  public static object? GetOrDefault(this Dictionary<string, object?> dict, string key) =>
    dict.TryGetValue(key, out var value) ? value : null;

  /// <summary>
  /// Looks up a captured parameter dict by its Revit <c>BuiltInParameter</c> name (e.g. "WALL_USER_HEIGHT_PARAM")
  /// within <c>properties["Parameters"][bucket][*][internalDefinitionName]</c>, searching across all groups.
  /// </summary>
  public static bool TryGetParameter(
    Base source,
    string bucket,
    string internalDefinitionName,
    out Dictionary<string, object>? parameter
  )
  {
    parameter = null;

    if (
      source["properties"] is not Dictionary<string, object?> properties
      || properties.GetOrDefault("Parameters") is not Dictionary<string, object> buckets
      || buckets.GetOrDefault(bucket) is not Dictionary<string, object> groups
    )
    {
      return false;
    }

    foreach (object groupObj in groups.Values)
    {
      if (groupObj is not Dictionary<string, object> group)
      {
        continue;
      }

      foreach (object paramObj in group.Values)
      {
        if (
          paramObj is Dictionary<string, object> param
          && param.GetOrDefault("internalDefinitionName") as string == internalDefinitionName
        )
        {
          parameter = param;
          return true;
        }
      }
    }

    return false;
  }

  /// <summary>Converts a deserialized numeric dynamic property value to double.</summary>
  public static bool TryToDouble(object? value, out double result)
  {
    switch (value)
    {
      case double d:
        result = d;
        return true;
      case float f:
        result = f;
        return true;
      case decimal m:
        result = (double)m;
        return true;
      case long l:
        result = l;
        return true;
      case int i:
        result = i;
        return true;
      case short s:
        result = s;
        return true;
      default:
        result = 0;
        return false;
    }
  }

  private static readonly Dictionary<string, double> UnitToMmFactor = new()
  {
    ["millimeters"] = 1,
    ["centimeters"] = 10,
    ["meters"] = 1000,
    ["feet"] = 304.8,
    ["inches"] = 25.4,
  };

  /// <summary>
  /// Converts a captured Revit parameter value to millimeters using its captured ForgeTypeId-style
  /// <paramref name="unitsTypeId"/> string (e.g. "autodesk.unit.unit:millimeters-1.0.1"), matched by
  /// <c>Contains</c> against known unit names. Returns the value unchanged (assumed already mm) if
  /// the unit is unrecognized.
  /// </summary>
  public static double ConvertToMm(double value, string? unitsTypeId)
  {
    foreach (var entry in UnitToMmFactor)
    {
      if (unitsTypeId != null && unitsTypeId.IndexOf(entry.Key, StringComparison.OrdinalIgnoreCase) >= 0)
      {
        return value * entry.Value;
      }
    }
    return value;
  }

  /// <summary>
  /// Converts a captured Revit angle parameter value to degrees using its captured
  /// ForgeTypeId-style unit id. Assumes degrees when the unit is unrecognized (Revit's common
  /// display unit for angles).
  /// </summary>
  public static double ConvertToDegrees(double value, string? unitsTypeId)
  {
    if (unitsTypeId != null && unitsTypeId.IndexOf("radian", StringComparison.OrdinalIgnoreCase) >= 0)
    {
      return value * 180.0 / Math.PI;
    }
    if (unitsTypeId != null && unitsTypeId.IndexOf("gradian", StringComparison.OrdinalIgnoreCase) >= 0)
    {
      return value * 0.9;
    }
    return value;
  }

  /// <summary>
  /// Looks up the first captured length parameter matching any of <paramref name="names"/>
  /// (checked against both Type and Instance parameter buckets - family dimension parameters can
  /// live in either, and their names are locale-dependent), converted to millimeters.
  /// </summary>
  public static bool TryGetLengthParamMm(RevitObject target, string[] names, out double mm)
  {
    foreach (var bucket in new[] { "Type Parameters", "Instance Parameters" })
    {
      foreach (var name in names)
      {
        if (
          TryGetParameter(target, bucket, name, out var param)
          && TryToDouble(param!.GetOrDefault("value"), out var value)
        )
        {
          mm = ConvertToMm(value, param.GetOrDefault("unitsTypeId") as string);
          if (mm > 0)
          {
            return true;
          }
        }
      }
    }

    mm = 0;
    return false;
  }

  // Two numbers separated by x / * ×, not part of a longer number, and not a concrete grade
  // ("C30/37"): e.g. "STB 1000 x 500", "Stütze 30/30", "300x300".
  private static readonly System.Text.RegularExpressions.Regex s_sectionInName = new(
    @"(?<![Cc]\s?)(?<![\d.,])(\d+(?:[.,]\d+)?)\s*[x×*/]\s*(\d+(?:[.,]\d+)?)(?![\d.,])",
    System.Text.RegularExpressions.RegexOptions.Compiled
  );

  /// <summary>
  /// Parses a rectangular cross-section from a Revit family/type name like "STB 1000 x 500" or
  /// "Stütze 30/30" - German type-naming conventions commonly encode the section dimensions in
  /// the name, which survives locale/family-parameter differences. Small value pairs are assumed
  /// to be centimeters ("30/30" → 300*300mm), larger ones millimeters.
  /// </summary>
  public static bool TryParseSectionFromName(string name, out double widthMm, out double heightMm)
  {
    widthMm = 0;
    heightMm = 0;
    var match = s_sectionInName.Match(name);
    if (!match.Success)
    {
      return false;
    }

    if (
      !double.TryParse(
        match.Groups[1].Value.Replace(',', '.'),
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture,
        out double a
      )
      || !double.TryParse(
        match.Groups[2].Value.Replace(',', '.'),
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture,
        out double b
      )
      || a <= 0
      || b <= 0
    )
    {
      return false;
    }

    if (a <= 120 && b <= 120)
    {
      a *= 10;
      b *= 10;
    }

    widthMm = a;
    heightMm = b;
    return true;
  }

  /// <summary>
  /// Lists the captured parameter names of a bucket ("display[INTERNAL_NAME]" when they differ) -
  /// diagnostic aid for fallback warnings, so unmatched parameter names are visible to the user
  /// without a debugger.
  /// </summary>
  public static List<string> GetCapturedParameterNames(RevitObject target, string bucket)
  {
    var names = new List<string>();
    if (
      target["properties"] is not Dictionary<string, object?> properties
      || properties.GetOrDefault("Parameters") is not Dictionary<string, object> buckets
      || buckets.GetOrDefault(bucket) is not Dictionary<string, object> groups
    )
    {
      return names;
    }

    foreach (var groupObj in groups.Values)
    {
      if (groupObj is not Dictionary<string, object> group)
      {
        continue;
      }
      foreach (var entry in group)
      {
        string? internalName =
          entry.Value is Dictionary<string, object> param ? param.GetOrDefault("internalDefinitionName") as string : null;
        names.Add(
          internalName is null || internalName == entry.Key ? entry.Key : $"{entry.Key}[{internalName}]"
        );
      }
    }
    return names;
  }

  /// <summary>
  /// Scale factor to convert geometry captured in <paramref name="sourceUnits"/> (a Speckle units
  /// string, e.g. <see cref="Units.Meters"/>) into the Tekla model's units.
  /// </summary>
  public static double GetUnitScaleFactor(string sourceUnits, string teklaModelUnits) =>
    Units.GetConversionFactor(sourceUnits, teklaModelUnits);

  /// <summary>
  /// Computes the vertical (Z) extent of a RevitObject's <c>displayValue</c> mesh geometry, in
  /// millimeters. Used as a fallback for column height when level-offset parameters are
  /// unreliable (e.g. <c>FAMILY_TOP_LEVEL_OFFSET_PARAM</c>=0, the common case where a column's top
  /// is flush with its top level).
  /// </summary>
  public static bool TryGetDisplayValueHeightMm(RevitObject target, out double heightMm)
  {
    if (TryGetDisplayValueBBoxMm(target, out var bbox))
    {
      heightMm = bbox.MaxZ - bbox.MinZ;
      return true;
    }
    heightMm = 0;
    return false;
  }

  /// <summary>World-axis-aligned bounding box of a RevitObject's displayValue meshes, in millimeters.</summary>
  public readonly record struct BBoxMm(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
  {
    public double SizeX => MaxX - MinX;
    public double SizeY => MaxY - MinY;
    public double SizeZ => MaxZ - MinZ;
    public double CenterX => (MinX + MaxX) / 2;
    public double CenterY => (MinY + MaxY) / 2;
  }

  /// <summary>
  /// Computes the world-axis-aligned bounding box of a RevitObject's <c>displayValue</c> mesh
  /// geometry, in millimeters. The real captured geometry is the most locale/family-independent
  /// source of overall dimensions (e.g. pad footing plan size and thickness) - family dimension
  /// parameters have locale-dependent names ("Width" vs "Breite") that can't be matched reliably.
  /// </summary>
  public static bool TryGetDisplayValueBBoxMm(RevitObject target, out BBoxMm bbox)
  {
    double minX = double.MaxValue,
      minY = double.MaxValue,
      minZ = double.MaxValue;
    double maxX = double.MinValue,
      maxY = double.MinValue,
      maxZ = double.MinValue;
    string? units = null;

    foreach (var item in target.displayValue)
    {
      if (item is not SOG.Mesh mesh)
      {
        continue;
      }

      units ??= mesh.units;
      for (int i = 0; i + 2 < mesh.vertices.Count; i += 3)
      {
        minX = Math.Min(minX, mesh.vertices[i]);
        maxX = Math.Max(maxX, mesh.vertices[i]);
        minY = Math.Min(minY, mesh.vertices[i + 1]);
        maxY = Math.Max(maxY, mesh.vertices[i + 1]);
        minZ = Math.Min(minZ, mesh.vertices[i + 2]);
        maxZ = Math.Max(maxZ, mesh.vertices[i + 2]);
      }
    }

    if (units is null || maxZ < minZ)
    {
      bbox = default;
      return false;
    }

    double toMm = GetUnitScaleFactor(units, Units.Millimeters);
    bbox = new BBoxMm(minX * toMm, minY * toMm, minZ * toMm, maxX * toMm, maxY * toMm, maxZ * toMm);
    return true;
  }

  // Internal Revit parameter name for the "Structural Material" type/instance parameter exposed by
  // Revit's structural family templates. Checked as an Instance Parameter first (families commonly
  // expose it as an instance-level override), then as a Type Parameter.
  private const string STRUCTURAL_MATERIAL_PARAM = "STRUCTURAL_MATERIAL_PARAM";

  /// <summary>Reads the captured Revit structural material display name, if any.</summary>
  public static bool TryGetStructuralMaterialName(RevitObject target, out string? materialName)
  {
    materialName =
      (TryGetParameter(target, "Instance Parameters", STRUCTURAL_MATERIAL_PARAM, out var instParam)
        ? instParam!.GetOrDefault("value") as string
        : null)
      ?? (TryGetParameter(target, "Type Parameters", STRUCTURAL_MATERIAL_PARAM, out var typeParam)
        ? typeParam!.GetOrDefault("value") as string
        : null);

    return materialName is not null;
  }

  public static SOG.Point ScalePoint(SOG.Point point, double factor) =>
    new(point.x * factor, point.y * factor, point.z * factor, point.units);

  public static SOG.Line ScaleLine(SOG.Line line, double factor) =>
    new() { start = ScalePoint(line.start, factor), end = ScalePoint(line.end, factor), units = line.units };

  public static SOG.Polycurve ScalePolycurve(SOG.Polycurve polycurve, double factor)
  {
    var scaled = new List<ICurve>(polycurve.segments.Count);
    foreach (var segment in polycurve.segments)
    {
      if (segment is not SOG.Line line)
      {
        throw new ConversionException("Only straight-line segments are supported for unit scaling.");
      }
      scaled.Add(ScaleLine(line, factor));
    }
    return new SOG.Polycurve { segments = scaled, units = polycurve.units, closed = polycurve.closed };
  }

  // Tekla hard-caps a Contour at 99 points (ContourPointsCheck).
  private const int MaxContourPoints = 99;

  // Target angular step for tessellating "wide" arcs (those whose tangent lines don't converge to
  // a usable rounding corner) into multiple straight chords.
  private const double WideArcChordAngleRadians = Math.PI / 18; // 10 degrees
  private const int MinWideArcChords = 2;
  private const int MaxWideArcChords = 24;

  /// <summary>
  /// Flattens a polycurve boundary into a list of (vertex, roundingRadius) pairs suitable for a
  /// Tekla <see cref="TSM.Contour"/>, capped at <see cref="MaxContourPoints"/> total vertices.
  /// <see cref="SOG.Line"/> segments contribute their start point with no rounding (radius 0).
  /// <see cref="SOG.Arc"/> segments are represented as a single "virtual corner" vertex - the point
  /// where the arc's two tangent lines would meet - paired with the arc's radius, so the caller can
  /// apply a <see cref="TSM.Chamfer"/> of type CHAMFER_ROUNDING to round that corner back down to
  /// the original arc. This construction only works for arcs whose tangents actually converge
  /// (subtending less than ~180 degrees); wider arcs are instead tessellated into multiple
  /// unrounded chords (an n-chord approximation), scaled down if necessary to keep the contour's
  /// total vertex count within <see cref="MaxContourPoints"/>.
  /// Each entry contributes its leading vertex/vertices; the loop is implicitly closed by the
  /// first segment's leading vertex.
  /// </summary>
  public static List<(SOG.Point Point, double ChamferRadius)> PolycurveToScaledPoints(
    SOG.Polycurve polycurve,
    double factor
  )
  {
    // Pass 1: classify each segment. Lines and "narrow" arcs (whose tangent lines converge to a
    // single roundable corner) contribute exactly one fixed vertex; "wide" arcs (near-semicircular
    // or reflex, where the corner construction is degenerate) are tessellated into multiple
    // straight chords and contribute a variable, capped vertex count.
    var fixedVertices = new List<(SOG.Point Point, double ChamferRadius)>?[polycurve.segments.Count];
    var wideArcs = new List<(int SegmentIndex, SOG.Arc Arc, int IdealChords)>();

    for (int i = 0; i < polycurve.segments.Count; i++)
    {
      switch (polycurve.segments[i])
      {
        case SOG.Line line:
          fixedVertices[i] = [(ScalePoint(line.start, factor), 0)];
          break;
        case SOG.Arc arc:
          // Corner point where the arc's tangent lines meet: center + (midPoint - center) / cos(measure/2).
          double cosHalfAngle = Math.Cos(arc.measure / 2);
          if (cosHalfAngle > 0.05)
          {
            var center = arc.plane.origin;
            var corner = new SOG.Point(
              center.x + (arc.midPoint.x - center.x) / cosHalfAngle,
              center.y + (arc.midPoint.y - center.y) / cosHalfAngle,
              center.z + (arc.midPoint.z - center.z) / cosHalfAngle,
              arc.units
            );
            fixedVertices[i] = [(ScalePoint(corner, factor), arc.radius * factor)];
          }
          else
          {
            int idealChords = Math.Max(
              MinWideArcChords,
              Math.Min(MaxWideArcChords, (int)Math.Ceiling(arc.measure / WideArcChordAngleRadians))
            );
            wideArcs.Add((i, arc, idealChords));
          }
          break;
        default:
          throw new ConversionException(
            $"Unsupported curve segment type '{polycurve.segments[i].GetType().Name}' in Revit boundary."
          );
      }
    }

    // Pass 2: cap the total vertex count at MaxContourPoints by scaling down wide-arc chord counts
    // (proportionally to their ideal count, never below MinWideArcChords) if necessary.
    int fixedTotal = fixedVertices.Sum(v => v?.Count ?? 0);
    int idealWideTotal = wideArcs.Sum(w => w.IdealChords);
    int wideBudget = Math.Max(MaxContourPoints - fixedTotal, wideArcs.Count * MinWideArcChords);

    // Pass 3: materialize the final vertex list in segment order.
    var points = new List<(SOG.Point Point, double ChamferRadius)>(polycurve.segments.Count);
    for (int i = 0; i < polycurve.segments.Count; i++)
    {
      if (fixedVertices[i] is { } fixedForSegment)
      {
        points.AddRange(fixedForSegment);
        continue;
      }

      var (_, arc, idealChords) = wideArcs.First(w => w.SegmentIndex == i);
      int chordCount =
        idealWideTotal > wideBudget
          ? Math.Max(MinWideArcChords, (int)Math.Floor(idealChords * (double)wideBudget / idealWideTotal))
          : idealChords;

      points.AddRange(TessellateArc(arc, chordCount, factor));
    }

    return points;
  }

  /// <summary>
  /// Samples <paramref name="chordCount"/> vertices along <paramref name="arc"/> at parameters
  /// t = 0, 1/n, ..., (n-1)/n (excluding the endpoint, which belongs to the next segment), using
  /// P(t) = center + cos(t*measure)*u + sin(t*measure)*w, where u = start - center and w is solved
  /// from the midpoint so the formula holds without needing the arc plane's normal/handedness.
  /// </summary>
  private static List<(SOG.Point Point, double ChamferRadius)> TessellateArc(
    SOG.Arc arc,
    int chordCount,
    double factor
  )
  {
    var center = arc.plane.origin;
    double ux = arc.startPoint.x - center.x;
    double uy = arc.startPoint.y - center.y;
    double uz = arc.startPoint.z - center.z;

    double cosHalf = Math.Cos(arc.measure / 2);
    double sinHalf = Math.Sin(arc.measure / 2);
    if (Math.Abs(sinHalf) < 1e-3)
    {
      sinHalf = sinHalf >= 0 ? 1e-3 : -1e-3;
    }

    double wx = (arc.midPoint.x - center.x - (cosHalf * ux)) / sinHalf;
    double wy = (arc.midPoint.y - center.y - (cosHalf * uy)) / sinHalf;
    double wz = (arc.midPoint.z - center.z - (cosHalf * uz)) / sinHalf;

    var result = new List<(SOG.Point Point, double ChamferRadius)>(chordCount);
    for (int k = 0; k < chordCount; k++)
    {
      double angle = arc.measure * k / chordCount;
      double cos = Math.Cos(angle);
      double sin = Math.Sin(angle);
      var point = new SOG.Point(
        center.x + (cos * ux) + (sin * wx),
        center.y + (cos * uy) + (sin * wy),
        center.z + (cos * uz) + (sin * wz),
        arc.units
      );
      result.Add((ScalePoint(point, factor), 0));
    }
    return result;
  }
}
