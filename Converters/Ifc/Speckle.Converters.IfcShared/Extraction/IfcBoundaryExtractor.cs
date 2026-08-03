using Speckle.Converters.IfcShared.Geometry;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Extraction;

/// <summary>
/// Resolves a closed 2D boundary loop (e.g. a slab's <c>'FootPrint'</c> representation) down to its
/// ordered vertex points, in the element's own local coordinates - same convention as
/// <see cref="IfcProfileExtractor"/>/<see cref="IfcAxisExtractor"/>. Confirmed against the real file
/// (<c>rstadvancedsampleproject.ifc</c>): a slab's <c>FootPrint</c> item can be EITHER an
/// <c>IfcCompositeCurve</c> made of straight 2-point <c>IfcPolyline</c> segments, OR a plain
/// <c>IfcPolyline</c> directly (the more common case in that file - 3 of 5 slabs use this simpler
/// form; only the fix found from a live receive, not assumed from the first sample checked). A
/// composite-curve segment can ALSO be a rounded corner (<c>IfcTrimmedCurve</c> over an
/// <c>IfcCircle</c>, confirmed to occur) - rather than requiring a real arc/polycurve host-side
/// representation (`FloorToHostConverter` only accepts a straight-segment `ICurve`), this tessellates
/// the arc into a chord-approximating polyline, deliberately trading exact roundness for staying a
/// simple polygon - a live receive confirmed this trade-off is preferred over falling to `DirectShape`.
/// </summary>
public static class IfcBoundaryExtractor
{
  // One vertex per 2 degrees of sweep - a smoother approximation than the original 10deg step,
  // requested after a live receive that a coarser polyline was visibly too faceted.
  private const double ARC_TESSELLATION_STEP_DEGREES = 2.0;

  /// <summary>
  /// Resolves <paramref name="elementId"/>'s <c>'FootPrint'</c> representation to an ordered list of
  /// boundary points (not explicitly closed - the last point does not repeat the first, even if the
  /// underlying IFC data repeats it; callers that need a closed polygon should close it themselves).
  /// Bails out (returns <c>false</c>) if the representation is missing, or - for the composite-curve
  /// form - any segment isn't a simple 2-point <c>IfcPolyline</c> or a circular-arc
  /// <c>IfcTrimmedCurve</c> - never guesses at other curve types (ellipses, splines, ...).
  /// </summary>
  public static bool TryExtractClosedBoundary(StepGraph graph, uint elementId, out List<IfcVector3> points)
  {
    points = [];

    if (!IfcRepresentationResolver.TryResolveFirstItem(graph, elementId, "FootPrint", out var item, out _))
    {
      return false;
    }

    uint? curveId = IfcGeometryReaders.AsId(item);
    if (curveId is null || !graph.Lookup.TryGetValue(curveId.Value, out var node))
    {
      return false;
    }

    return TryReadCurveBoundary(graph, node.Entity, out points);
  }

  /// <summary>
  /// The IFCPOLYLINE/IFCCOMPOSITECURVE dispatch shared with <see cref="IfcProfileExtractor"/>'s own
  /// arbitrary-profile OuterCurve reader - a swept profile's OuterCurve can be either shape, same as a
  /// 'FootPrint' representation item, and both need the exact same tessellation-of-rounded-corners
  /// handling (see <see cref="TryAppendTessellatedArc"/>'s remarks).
  /// </summary>
  internal static bool TryReadCurveBoundary(StepGraph graph, StepInstance curveEntity, out List<IfcVector3> points)
  {
    if (curveEntity.IsEntityType("IFCPOLYLINE"))
    {
      return TryReadClosedPolyline(graph, curveEntity, out points);
    }

    if (curveEntity.IsEntityType("IFCCOMPOSITECURVE"))
    {
      return TryReadCompositeCurve(graph, curveEntity, out points);
    }

    points = [];
    return false;
  }

  private static bool TryReadCompositeCurve(StepGraph graph, StepInstance compositeCurve, out List<IfcVector3> points)
  {
    points = [];

    if (compositeCurve.Count == 0 || compositeCurve[0] is not StepList segments || segments.Values.Count == 0)
    {
      return false;
    }

    var result = new List<IfcVector3>(segments.Values.Count);
    foreach (var segmentValue in segments.Values)
    {
      if (!TryAppendSegmentPoints(graph, segmentValue, result))
      {
        return false;
      }
    }

    points = result;
    return true;
  }

  // A closed boundary IfcPolyline commonly repeats its first point as its last (confirmed in the real
  // file - e.g. a 13-point list closing a 12-vertex polygon) - stripped here so the returned list
  // matches this method's own "not explicitly closed" convention (same as the composite-curve path),
  // rather than leaving callers to deal with a degenerate zero-length final edge.
  private static bool TryReadClosedPolyline(StepGraph graph, StepInstance polyline, out List<IfcVector3> points)
  {
    points = [];

    if (polyline.Count == 0 || polyline[0] is not StepList curvePoints || curvePoints.Values.Count < 3)
    {
      return false;
    }

    var result = new List<IfcVector3>(curvePoints.Values.Count);
    foreach (var pointValue in curvePoints.Values)
    {
      if (
        IfcGeometryReaders.AsId(pointValue) is not { } pointId
        || !IfcGeometryReaders.TryReadCartesianPoint(graph, pointId, out var point)
      )
      {
        return false;
      }
      result.Add(point);
    }

    if (result.Count > 1 && IsSamePoint(result[0], result[^1]))
    {
      result.RemoveAt(result.Count - 1);
    }

    if (result.Count < 3)
    {
      return false;
    }

    points = result;
    return true;
  }

  private const double SAME_POINT_TOLERANCE_MM = 1e-6;

  private static bool IsSamePoint(IfcVector3 a, IfcVector3 b) =>
    Math.Abs(a.X - b.X) < SAME_POINT_TOLERANCE_MM
    && Math.Abs(a.Y - b.Y) < SAME_POINT_TOLERANCE_MM
    && Math.Abs(a.Z - b.Z) < SAME_POINT_TOLERANCE_MM;

  // IfcCompositeCurveSegment(Transition, SameSense, ParentCurve) - a straight 2-point IfcPolyline
  // contributes just its own start point; a circular-arc IfcTrimmedCurve contributes a tessellated
  // chord approximation. Either way, consecutive segments share an endpoint (segment N's end ==
  // segment N+1's start), so the segment's own END point is never added here - the loop closes itself
  // once every segment has contributed its points.
  private static bool TryAppendSegmentPoints(StepGraph graph, StepValue segmentValue, List<IfcVector3> result)
  {
    if (
      IfcGeometryReaders.AsId(segmentValue) is not { } segmentId
      || !graph.Lookup.TryGetValue(segmentId, out var segmentNode)
      || !segmentNode.Entity.IsEntityType("IFCCOMPOSITECURVESEGMENT")
      || segmentNode.Entity.Count < 3
      || segmentNode.Entity[1] is not StepSymbol sameSenseSymbol
      || IfcGeometryReaders.AsId(segmentNode.Entity[2]) is not { } parentCurveId
      || !graph.Lookup.TryGetValue(parentCurveId, out var parentCurveNode)
    )
    {
      return false;
    }

    // IfcCompositeCurveSegment's SameSense (attribute index 1): whether this segment is traversed in
    // the composite curve in its underlying curve's own natural direction (.T.) or reversed (.F.) -
    // confirmed to actually occur .F. in the real file (a rounded-corner arc), and ignoring it produces
    // a tessellation that sweeps ~290 degrees the wrong way instead of the intended ~69 degree fillet -
    // self-intersecting geometry that Revit's Floor.Create correctly rejects ("curve loops cannot
    // compose a valid boundary").
    bool sameSense = sameSenseSymbol.Name.ToString().Equals("T", StringComparison.OrdinalIgnoreCase);

    if (parentCurveNode.Entity.IsEntityType("IFCPOLYLINE"))
    {
      if (
        parentCurveNode.Entity.Count < 1
        || parentCurveNode.Entity[0] is not StepList curvePoints
        || curvePoints.Values.Count < 2
        || IfcGeometryReaders.AsId(curvePoints.Values[sameSense ? 0 : ^1]) is not { } firstPointId
        || !IfcGeometryReaders.TryReadCartesianPoint(graph, firstPointId, out var start)
      )
      {
        return false;
      }

      result.Add(start);
      return true;
    }

    if (parentCurveNode.Entity.IsEntityType("IFCTRIMMEDCURVE"))
    {
      return TryAppendTessellatedArc(graph, parentCurveNode.Entity, sameSense, result);
    }

    // e.g. an ellipse or spline segment - not yet supported, never guessed.
    return false;
  }

  // IfcTrimmedCurve(BasisCurve, Trim1, Trim2, SenseAgreement, MasterRepresentation) over an IfcCircle.
  // Confirmed against the real file: this project's plane angle unit is DEGREE (via
  // IfcUnitAssignment/IfcConversionBasedUnit - not the IFC schema's nominal default of radians), and
  // .PARAMETER. trim values are plain angle numbers in that unit - hardcoded here rather than reading
  // IfcUnitAssignment generically, a known simplification. Supports both .PARAMETER. (angle values
  // given directly) and .CARTESIAN. (trim points - angle derived from their position relative to the
  // circle's own center) master representations.
  private static bool TryAppendTessellatedArc(
    StepGraph graph,
    StepInstance trimmedCurve,
    bool sameSense,
    List<IfcVector3> result
  )
  {
    if (
      trimmedCurve.Count < 5
      || IfcGeometryReaders.AsId(trimmedCurve[0]) is not { } basisCurveId
      || !graph.Lookup.TryGetValue(basisCurveId, out var basisNode)
      || !basisNode.Entity.IsEntityType("IFCCIRCLE")
      || basisNode.Entity.Count < 2
      || !IfcGeometryReaders.TryReadNumber(basisNode.Entity[1], out double radius)
      || trimmedCurve[4] is not StepSymbol masterRepresentation
    )
    {
      return false;
    }

    IfcPlacement circleFrame = ReadCircleAxisPlacement(graph, basisNode.Entity[0]);

    string masterRepName = masterRepresentation.Name.ToString();
    double startAngleDegrees;
    double endAngleDegrees;
    if (masterRepName == "PARAMETER")
    {
      if (
        !TryReadParameterAngle(trimmedCurve[1], out startAngleDegrees)
        || !TryReadParameterAngle(trimmedCurve[2], out endAngleDegrees)
      )
      {
        return false;
      }
    }
    else if (masterRepName == "CARTESIAN")
    {
      if (
        !TryReadCartesianTrimAngle(graph, trimmedCurve[1], circleFrame, out startAngleDegrees)
        || !TryReadCartesianTrimAngle(graph, trimmedCurve[2], circleFrame, out endAngleDegrees)
      )
      {
        return false;
      }
    }
    else
    {
      return false;
    }

    // SameSense=.F. means this segment is traversed in the COMPOSITE curve backwards relative to its
    // own Trim1->Trim2 direction - confirmed to actually occur in the real file (a rounded corner).
    // Without this swap, a segment like this ("Trim1=290deg, Trim2=0deg, SameSense=.F.") tessellates
    // sweeping ~290 degrees the wrong way around instead of the intended ~69 degree fillet.
    if (!sameSense)
    {
      (startAngleDegrees, endAngleDegrees) = (endAngleDegrees, startAngleDegrees);
    }

    // Normalize to the shortest angular path (-180, 180] - a corner-rounding fillet is always the
    // short way around, never the reflex/long way, regardless of the raw trim values' own numeric
    // difference (which can cross the 0/360 wrap point, as the real file's case above does: naively
    // 0 - 290.63 = -290.63 degrees, the wrong, ~290-degree-long way around, instead of the intended
    // +69.37 degrees).
    double sweepDegrees = endAngleDegrees - startAngleDegrees;
    if (sweepDegrees > 180)
    {
      sweepDegrees -= 360;
    }
    else if (sweepDegrees <= -180)
    {
      sweepDegrees += 360;
    }

    int segmentCount = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweepDegrees) / ARC_TESSELLATION_STEP_DEGREES));
    // Only i in [0, segmentCount) - the final point (i == segmentCount, the arc's own end) is left for
    // the next composite-curve segment to supply as its own start, same convention as the polyline case.
    for (int i = 0; i < segmentCount; i++)
    {
      double angleDegrees = startAngleDegrees + sweepDegrees * i / segmentCount;
      double angleRadians = angleDegrees * Math.PI / 180.0;
      var localPoint = new IfcVector3(radius * Math.Cos(angleRadians), radius * Math.Sin(angleRadians), 0);
      result.Add(circleFrame.TransformPoint(localPoint));
    }

    return true;
  }

  // IfcCircle(Position, Radius) - Position is an IfcAxis2Placement2D (or 3D, not seen in practice for a
  // 2D FootPrint boundary). Degrades to identity if unreadable - same soft-degrade pattern as
  // IfcProfileExtractor's own Position handling. Internal (not private) so IfcAxisExtractor can reuse
  // it for a curved wall's own bare IfcTrimmedCurve Axis representation - same IfcCircle+PARAMETER-trim
  // shape, just not wrapped in a composite curve segment.
  internal static IfcPlacement ReadCircleAxisPlacement(StepGraph graph, StepValue positionValue)
  {
    if (
      IfcGeometryReaders.AsId(positionValue) is not { } positionId
      || !graph.Lookup.TryGetValue(positionId, out var positionNode)
      || !positionNode.Entity.IsEntityType("IFCAXIS2PLACEMENT2D")
      || positionNode.Entity.Count < 1
      || IfcGeometryReaders.AsId(positionNode.Entity[0]) is not { } locationId
      || !IfcGeometryReaders.TryReadCartesianPoint(graph, locationId, out var location)
    )
    {
      return IfcPlacement.Identity;
    }

    IfcVector3? refDirection = null;
    if (
      positionNode.Entity.Count > 1
      && IfcGeometryReaders.AsId(positionNode.Entity[1]) is { } refDirectionId
      && IfcGeometryReaders.TryReadDirection(graph, refDirectionId, out var direction)
    )
    {
      refDirection = direction;
    }

    return IfcPlacement.FromAxis2Placement2D(location, refDirection);
  }

  // Trim1/Trim2 for .PARAMETER. representation are each a 1-item SET containing an inline
  // IFCPARAMETERVALUE(x) - a STEP "defined type" instantiation, parsed by this project's tokenizer as
  // an ordinary nested StepEntity (see StepFactory.Create's Ident case), not a #id reference. Internal
  // (not private) - shared with IfcAxisExtractor, see ReadCircleAxisPlacement's remarks.
  internal static bool TryReadParameterAngle(StepValue trimSetValue, out double angleDegrees)
  {
    angleDegrees = 0;

    if (
      trimSetValue is not StepList trimSet
      || trimSet.Values.Count == 0
      || trimSet.Values[0] is not StepEntity parameterValueEntity
      || !parameterValueEntity.EntityType.ToString().Equals("IFCPARAMETERVALUE", StringComparison.OrdinalIgnoreCase)
      || parameterValueEntity.Attributes.Values.Count == 0
      || !IfcGeometryReaders.TryReadNumber(parameterValueEntity.Attributes.Values[0], out angleDegrees)
    )
    {
      return false;
    }

    return true;
  }

  // .CARTESIAN. representation: Trim1/Trim2 are each a 1-item SET containing an IfcCartesianPoint
  // reference directly - derive the angle from the point's position relative to the circle's own
  // center/frame instead of trusting a parametric value that isn't present in this representation.
  private static bool TryReadCartesianTrimAngle(
    StepGraph graph,
    StepValue trimSetValue,
    IfcPlacement circleFrame,
    out double angleDegrees
  )
  {
    angleDegrees = 0;

    if (
      trimSetValue is not StepList trimSet
      || trimSet.Values.Count == 0
      || IfcGeometryReaders.AsId(trimSet.Values[0]) is not { } pointId
      || !IfcGeometryReaders.TryReadCartesianPoint(graph, pointId, out var point)
    )
    {
      return false;
    }

    var local = point - circleFrame.Origin;
    double localX = local.Dot(circleFrame.XAxis);
    double localY = local.Dot(circleFrame.YAxis);
    angleDegrees = Math.Atan2(localY, localX) * 180.0 / Math.PI;
    return true;
  }
}
