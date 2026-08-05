using Speckle.Converters.IfcShared.Geometry;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Extraction;

/// <summary>
/// Resolves an element's <c>'Axis'</c> representation (e.g. a beam's baseline) down to its start/end
/// points, in the element's own LOCAL coordinates - same convention as <c>'Body'</c> geometry (see
/// <see cref="IfcProfileExtractor"/>'s remarks). Callers must transform both points through the
/// element's absolute placement (<see cref="IfcPlacementResolver.TryResolveElementPlacement"/> +
/// <see cref="IfcPlacement.TransformPoint"/>) to get world-space coordinates - this extractor
/// deliberately only reads geometry, matching the existing split between profile/axis extraction and
/// placement resolution elsewhere in this project.
/// </summary>
public static class IfcAxisExtractor
{
  /// <summary>
  /// Resolves the two endpoints of an axis curve. Handles THREE forms found across this feature's
  /// live-file investigation: a simple 2-point <c>IfcPolyline</c> (confirmed for Revit-authored walls),
  /// an <c>IfcTrimmedCurve</c> with <c>MasterRepresentation=.CARTESIAN.</c> (confirmed for Revit beams),
  /// and an <c>IfcIndexedPolyCurve</c> of straight <c>IfcLineIndex</c> segment(s) (confirmed for a real
  /// ArchiCAD 26-authored wall - a shared 2D point list plus a single <c>IFCLINEINDEX((1,2))</c>
  /// segment). Bails out on anything else (a multi-segment polyline with more than 2 points, a
  /// parametrically-trimmed curve, an arc segment in the indexed poly curve, any curve type this
  /// doesn't recognize) rather than guessing.
  /// </summary>
  public static bool TryExtractAxisLine(StepGraph graph, uint elementId, out IfcVector3 start, out IfcVector3 end)
  {
    start = IfcVector3.Zero;
    end = IfcVector3.Zero;

    if (!IfcRepresentationResolver.TryResolveFirstItem(graph, elementId, "Axis", out var item, out _))
    {
      return false;
    }

    uint? curveId = IfcGeometryReaders.AsId(item);
    if (curveId is null || !graph.Lookup.TryGetValue(curveId.Value, out var node))
    {
      return false;
    }

    if (node.Entity.IsEntityType("IFCPOLYLINE"))
    {
      return TryReadPolylineEndpoints(graph, node.Entity, out start, out end);
    }

    if (node.Entity.IsEntityType("IFCTRIMMEDCURVE"))
    {
      return TryReadTrimmedCurveEndpoints(graph, node.Entity, out start, out end);
    }

    if (node.Entity.IsEntityType("IFCINDEXEDPOLYCURVE"))
    {
      return TryReadIndexedPolyCurveEndpoints(graph, node.Entity, out start, out end);
    }

    return false;
  }

  /// <summary>
  /// Resolves a CURVED axis: an <c>'Axis'</c> representation whose curve is either an
  /// <c>IfcIndexedPolyCurve</c> with exactly one <c>IfcArcIndex</c> segment (three point-list indices -
  /// start, a point ON the arc, end - confirmed common in a real ArchiCAD 26 export, roughly a quarter
  /// of that file's walls use this form), or a bare <c>IfcTrimmedCurve</c> over an <c>IfcCircle</c>
  /// (confirmed for a real Revit-exported curved wall - distinct from the ArchiCAD form, not wrapped in
  /// any composite/indexed curve at all). Both forms are common enough that <see cref="TryExtractAxisLine"/>
  /// can't afford to keep bailing out on them. Deliberately narrow, mirroring every other extractor's
  /// "never guess" rule: more than one segment, a segment that isn't a single arc, or a trim
  /// representation other than <c>.PARAMETER.</c> still bails out - this only recognizes the exact
  /// shapes confirmed in real data. The three returned points map directly onto Revit's own
  /// <c>DB.Arc.Create(XYZ,XYZ,XYZ)</c> three-point-arc constructor - no circumcenter/plane computation
  /// is needed here at all, unlike the tessellation this project's profile/boundary extractors perform
  /// for footprint/profile arcs.
  /// </summary>
  public static bool TryExtractAxisArc(
    StepGraph graph,
    uint elementId,
    out IfcVector3 start,
    out IfcVector3 mid,
    out IfcVector3 end
  )
  {
    start = IfcVector3.Zero;
    mid = IfcVector3.Zero;
    end = IfcVector3.Zero;

    if (!IfcRepresentationResolver.TryResolveFirstItem(graph, elementId, "Axis", out var item, out _))
    {
      return false;
    }

    uint? curveId = IfcGeometryReaders.AsId(item);
    if (curveId is null || !graph.Lookup.TryGetValue(curveId.Value, out var node))
    {
      return false;
    }

    if (node.Entity.IsEntityType("IFCTRIMMEDCURVE"))
    {
      return TryReadTrimmedCircleArc(graph, node.Entity, out start, out mid, out end);
    }

    if (!node.Entity.IsEntityType("IFCINDEXEDPOLYCURVE"))
    {
      return false;
    }

    var indexedPolyCurve = node.Entity;
    if (
      indexedPolyCurve.Count < 2
      || IfcGeometryReaders.AsId(indexedPolyCurve[0]) is not { } pointListId
      || !IfcGeometryReaders.TryReadCartesianPointList2D(graph, pointListId, out var points)
      || indexedPolyCurve[1] is not StepList segments
      || segments.Values.Count != 1
      || segments.Values[0] is not StepEntity arcSegment
      || !arcSegment.EntityType.ToString().Equals("IFCARCINDEX", StringComparison.OrdinalIgnoreCase)
      || arcSegment.Attributes.Values.Count == 0
      || arcSegment.Attributes.Values[0] is not StepList indices
    )
    {
      return false;
    }

    // IfcArcIndex is always exactly 3 indices (start, a point ON the arc, end) - anything else is a
    // malformed file, not guessed at. CA1508 misfires here (it cannot see that `indices` is an
    // independent StepList from `segments` above), so it's narrowly suppressed rather than removing a
    // real, needed runtime check.
#pragma warning disable CA1508
    if (indices.Values.Count != 3)
#pragma warning restore CA1508
    {
      return false;
    }

    if (
      !IfcGeometryReaders.TryReadNumber(indices.Values[0], out double startIndex)
      || !IfcGeometryReaders.TryReadNumber(indices.Values[1], out double midIndex)
      || !IfcGeometryReaders.TryReadNumber(indices.Values[2], out double endIndex)
    )
    {
      return false;
    }

    int startPointIndex = (int)startIndex - 1;
    int midPointIndex = (int)midIndex - 1;
    int endPointIndex = (int)endIndex - 1;
    if (
      startPointIndex < 0
      || startPointIndex >= points.Count
      || midPointIndex < 0
      || midPointIndex >= points.Count
      || endPointIndex < 0
      || endPointIndex >= points.Count
    )
    {
      return false;
    }

    start = points[startPointIndex];
    mid = points[midPointIndex];
    end = points[endPointIndex];
    return true;
  }

  // IfcTrimmedCurve(BasisCurve, Trim1, Trim2, SenseAgreement, MasterRepresentation) directly over an
  // IfcCircle, used as a wall's 'Axis' representation with no composite/indexed-curve wrapper at all -
  // confirmed for a real Revit-exported curved wall (distinct from IfcBoundaryExtractor's own
  // trimmed-arc reader, which only ever sees one as a segment INSIDE a composite curve, e.g. a slab's
  // FootPrint rounded corner). Reuses the same circle-frame/PARAMETER-angle readers
  // (IfcBoundaryExtractor.ReadCircleAxisPlacement/TryReadParameterAngle) since the underlying IfcCircle
  // shape and this project's plane-angle-unit-is-degrees assumption are identical either way. Only
  // .PARAMETER. trims are supported (matches both real curved walls sampled) - .CARTESIAN. would need
  // the same point-relative-to-center angle derivation IfcBoundaryExtractor.TryReadCartesianTrimAngle
  // already does, not yet needed here since no real sample has required it.
  private static bool TryReadTrimmedCircleArc(
    StepGraph graph,
    StepInstance trimmedCurve,
    out IfcVector3 start,
    out IfcVector3 mid,
    out IfcVector3 end
  )
  {
    start = IfcVector3.Zero;
    mid = IfcVector3.Zero;
    end = IfcVector3.Zero;

    if (
      trimmedCurve.Count < 5
      || IfcGeometryReaders.AsId(trimmedCurve[0]) is not { } basisCurveId
      || !graph.Lookup.TryGetValue(basisCurveId, out var basisCurveNode)
      || !basisCurveNode.Entity.IsEntityType("IFCCIRCLE")
      || basisCurveNode.Entity.Count < 2
      || !IfcGeometryReaders.TryReadNumber(basisCurveNode.Entity[1], out double radius)
      || trimmedCurve[4] is not StepSymbol masterRepresentation
      || masterRepresentation.Name.ToString() != "PARAMETER"
      || !IfcBoundaryExtractor.TryReadParameterAngle(trimmedCurve[1], out double startAngleDegrees)
      || !IfcBoundaryExtractor.TryReadParameterAngle(trimmedCurve[2], out double endAngleDegrees)
    )
    {
      return false;
    }

    var circleFrame = IfcBoundaryExtractor.ReadCircleAxisPlacement(graph, basisCurveNode.Entity[0]);

    // Normalize to the shortest angular path (-180, 180] - same reasoning as
    // IfcBoundaryExtractor.TryAppendTessellatedArc: the raw trim values can cross the 0/360 wrap point
    // (e.g. 270 -> 359.999 is a genuine +90 degree sweep, not the long way around), and a wall's own
    // curve is always the short way around, never reflex.
    double sweepDegrees = endAngleDegrees - startAngleDegrees;
    if (sweepDegrees > 180)
    {
      sweepDegrees -= 360;
    }
    else if (sweepDegrees <= -180)
    {
      sweepDegrees += 360;
    }
    double midAngleDegrees = startAngleDegrees + (sweepDegrees / 2);

    start = PointOnCircle(circleFrame, radius, startAngleDegrees);
    mid = PointOnCircle(circleFrame, radius, midAngleDegrees);
    end = PointOnCircle(circleFrame, radius, endAngleDegrees);
    return true;
  }

  private static IfcVector3 PointOnCircle(IfcPlacement circleFrame, double radius, double angleDegrees)
  {
    double angleRadians = angleDegrees * Math.PI / 180.0;
    return circleFrame.TransformPoint(
      new IfcVector3(radius * Math.Cos(angleRadians), radius * Math.Sin(angleRadians), 0)
    );
  }

  // IfcIndexedPolyCurve(Points, Segments, SelfIntersect) - only a straight (possibly multi-waypoint)
  // axis is supported: every segment must be an IFCLINEINDEX (an arc segment bails out rather than
  // guessing, same "never guess" rule as everywhere else), and the overall axis start/end are simply
  // the shared point list's first and last points - a wall's axis only ever needs its two ends, not
  // the shape of any intermediate waypoints.
  private static bool TryReadIndexedPolyCurveEndpoints(
    StepGraph graph,
    StepInstance indexedPolyCurve,
    out IfcVector3 start,
    out IfcVector3 end
  )
  {
    start = IfcVector3.Zero;
    end = IfcVector3.Zero;

    if (
      indexedPolyCurve.Count < 2
      || IfcGeometryReaders.AsId(indexedPolyCurve[0]) is not { } pointListId
      || !IfcGeometryReaders.TryReadCartesianPointList2D(graph, pointListId, out var points)
      || points.Count < 2
      || indexedPolyCurve[1] is not StepList segments
      || segments.Values.Count == 0
    )
    {
      return false;
    }

    foreach (var segmentValue in segments.Values)
    {
      if (
        segmentValue is not StepEntity segmentEntity
        || !segmentEntity.EntityType.ToString().Equals("IFCLINEINDEX", StringComparison.OrdinalIgnoreCase)
      )
      {
        // e.g. an IFCARCINDEX segment - not a straight axis, never guessed.
        return false;
      }
    }

    start = points[0];
    end = points[^1];
    return true;
  }

  // IfcPolyline(Points) - only the simple 2-point case is supported; a real multi-segment polyline
  // is left unhandled rather than guessed at (which segment would even be "the" axis?).
  private static bool TryReadPolylineEndpoints(
    StepGraph graph,
    StepInstance polyline,
    out IfcVector3 start,
    out IfcVector3 end
  )
  {
    start = IfcVector3.Zero;
    end = IfcVector3.Zero;

    if (polyline.Count < 1 || polyline[0] is not StepList points || points.Values.Count != 2)
    {
      return false;
    }

    uint? startId = IfcGeometryReaders.AsId(points.Values[0]);
    uint? endId = IfcGeometryReaders.AsId(points.Values[1]);

    return startId is not null
      && endId is not null
      && IfcGeometryReaders.TryReadCartesianPoint(graph, startId.Value, out start)
      && IfcGeometryReaders.TryReadCartesianPoint(graph, endId.Value, out end);
  }

  // IfcTrimmedCurve(BasisCurve, Trim1, Trim2, SenseAgreement, MasterRepresentation). Trim1/Trim2 are
  // each a 1-item SET (IFC's STEP encoding for a SELECT type) - only the .CARTESIAN. master
  // representation is supported, where that item is an IfcCartesianPoint reference directly.
  // .PARAMETER. (a parametric distance along the basis curve) is NOT supported - would require
  // evaluating arbitrary curve types.
  //
  // BUG FIX: this used to trust the two cartesian trim points as "the" start/end unconditionally,
  // reasoning that the BasisCurve "never needs evaluating" - true only when BasisCurve is itself a
  // straight IfcLine (the trim points ARE the segment's endpoints). For a curved beam, BasisCurve is
  // an IfcCircle (or other curve) and the two trim points are only the chord endpoints - silently
  // treating that chord as the beam's straight axis produces a plausible-but-wrong straight line
  // instead of bailing out. Found from a live receive where a curved beam came through wrong rather
  // than falling back to DirectShape. Now requires BasisCurve to be an IfcLine before trusting the
  // trim points as a straight segment.
  private static bool TryReadTrimmedCurveEndpoints(
    StepGraph graph,
    StepInstance trimmedCurve,
    out IfcVector3 start,
    out IfcVector3 end
  )
  {
    start = IfcVector3.Zero;
    end = IfcVector3.Zero;

    if (trimmedCurve.Count < 5)
    {
      return false;
    }

    uint? basisCurveId = IfcGeometryReaders.AsId(trimmedCurve[0]);
    if (
      basisCurveId is null
      || !graph.Lookup.TryGetValue(basisCurveId.Value, out var basisCurveNode)
      || !basisCurveNode.Entity.IsEntityType("IFCLINE")
    )
    {
      return false;
    }

    if (trimmedCurve[4] is not StepSymbol masterRep || masterRep.Name.ToString() != "CARTESIAN")
    {
      return false;
    }

    return TryReadCartesianTrim(graph, trimmedCurve[1], out start)
      && TryReadCartesianTrim(graph, trimmedCurve[2], out end);
  }

  private static bool TryReadCartesianTrim(StepGraph graph, StepValue trimSetValue, out IfcVector3 point)
  {
    point = IfcVector3.Zero;

    if (trimSetValue is not StepList trimSet || trimSet.Values.Count == 0)
    {
      return false;
    }

    uint? pointId = IfcGeometryReaders.AsId(trimSet.Values[0]);
    return pointId is not null && IfcGeometryReaders.TryReadCartesianPoint(graph, pointId.Value, out point);
  }
}
