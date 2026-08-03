using Speckle.Converters.IfcShared.Geometry;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Extraction;

/// <summary>
/// Resolves an element's extruded cross-section profile, starting from its <c>Representation</c>
/// attribute and walking down to the underlying <c>IfcProfileDef</c>. Handles the
/// <c>MappedRepresentation</c> indirection Revit commonly emits for repeated family types (confirmed
/// on the real round columns in this feature's live-file investigation - see the plan's "Findings
/// from Live File Inspection") by following one level of <c>IfcMappedItem</c> -&gt;
/// <c>IfcRepresentationMap</c> -&gt; <c>MappedRepresentation</c> indirection to reach the real geometry.
/// </summary>
/// <remarks>
/// A <c>MappedItem</c>'s own transform (<c>MappingTarget</c>) is still not applied here - confirmed
/// identity in every real-file sample checked so far, but not guaranteed in general. What IS applied
/// (fixed after a live receive put every instance of a repeated column type at the exact same world
/// position): the <c>IfcExtrudedAreaSolid</c>'s own <c>Position</c> attribute, returned as
/// <c>localPosition</c> in <see cref="TryResolveExtrudedProfile(StepGraph,uint,out uint,out double,out IfcPlacement)"/>. In at least one real
/// export (Revit, this feature's test file), every instance's <c>ObjectPlacement</c> AND the
/// <c>IfcRepresentationMap</c>'s <c>MappingOrigin</c> were identical/identity for every column of a
/// repeated type - the actual per-instance world offset lived entirely in this <c>Position</c>
/// attribute instead. Callers must compose it through the element's absolute placement (see
/// <see cref="IfcPlacementResolver.TryResolveElementPlacement"/> + <see cref="IfcPlacement.TransformPoint"/>)
/// to get the true world origin - reading only <c>ObjectPlacement</c> is not sufficient.
/// <para/>
/// KNOWN LIMITATION: assumes the file's raw numeric values are already in millimeters (true for the
/// file this was built and tested against). A fully correct implementation would read the project's
/// declared length unit (<c>IfcUnitAssignment</c>/<c>IfcSIUnit</c>) and convert - not yet implemented.
/// </remarks>
public static class IfcProfileExtractor
{
  /// <summary>
  /// Resolves <paramref name="elementId"/>'s (e.g. an <c>IfcColumn</c>) <c>'Body'</c>
  /// <c>IfcExtrudedAreaSolid</c>, returning the express id of its swept profile definition, the
  /// extrusion depth (mm), and the extrusion's own local <c>Position</c> (see this class's remarks -
  /// this is NOT redundant with the element's <c>ObjectPlacement</c>, at least for one real export).
  /// Bails out (returns <c>false</c>) on anything that isn't a clean single extrusion - e.g. an
  /// <c>AdvancedBrep</c> or <c>CSG</c> body (confirmed to occur for beams in the same live file that
  /// had clean columns) - never guesses.
  /// </summary>
  public static bool TryResolveExtrudedProfile(
    StepGraph graph,
    uint elementId,
    out uint profileDefId,
    out double depthMm,
    out IfcPlacement localPosition
  ) => TryResolveExtrudedProfile(graph, elementId, out profileDefId, out depthMm, out localPosition, out _);

  /// <summary>
  /// Overload additionally returning the extrusion's own <c>ExtrudedDirection</c> - added for a
  /// geometry-derived axis fallback (see <c>RevitNativeSchemaEnricher</c>'s beam/wall enrichment): an
  /// element with no declared <c>'Axis'</c> representation but a clean extrusion can still get a real
  /// axis line from <c>Position.Origin -&gt; Position.Origin + ExtrudedDirection * Depth</c>, the same
  /// technique already used for a footing's vertical line, generalized to any direction.
  /// </summary>
  public static bool TryResolveExtrudedProfile(
    StepGraph graph,
    uint elementId,
    out uint profileDefId,
    out double depthMm,
    out IfcPlacement localPosition,
    out IfcVector3 extrudedDirection
  )
  {
    profileDefId = 0;
    depthMm = 0;
    localPosition = IfcPlacement.Identity;
    extrudedDirection = IfcVector3.UnitZ;

    if (!IfcRepresentationResolver.TryResolveFirstItem(graph, elementId, "Body", out var item, out string type))
    {
      return false;
    }

    if (type != "SweptSolid")
    {
      // e.g. "AdvancedBrep", "CSG", "Brep", "Tessellation" - not a clean extrusion, nothing to extract.
      return false;
    }

    uint? extrudedSolidId = IfcGeometryReaders.AsId(item);

    if (
      extrudedSolidId is null
      || !graph.Lookup.TryGetValue(extrudedSolidId.Value, out var solidNode)
      || !solidNode.Entity.IsEntityType("IFCEXTRUDEDAREASOLID")
      || solidNode.Entity.Count < 4
    )
    {
      return false;
    }

    // IfcExtrudedAreaSolid(SweptArea, Position, ExtrudedDirection, Depth)
    uint? profileId = IfcGeometryReaders.AsId(solidNode.Entity[0]);
    if (profileId is null || !IfcGeometryReaders.TryReadNumber(solidNode.Entity[3], out double depth))
    {
      return false;
    }

    // Position is a required IfcAxis2Placement3D per the IFC4 schema - if it fails to resolve for any
    // reason, degrade to identity (this class's previous behavior) rather than failing the whole
    // extraction, since the profile/depth are still perfectly usable on their own.
    uint? positionId = IfcGeometryReaders.AsId(solidNode.Entity[1]);
    if (
      positionId is not null
      && IfcPlacementResolver.TryResolveAxis2Placement3DById(graph, positionId.Value, out var resolvedPosition)
      && resolvedPosition is not null
    )
    {
      localPosition = resolvedPosition;
    }

    // ExtrudedDirection is required by the schema - degrade to +Z (this method's prior implicit
    // assumption) if it fails to resolve for any reason, same soft-degrade philosophy as Position.
    if (
      IfcGeometryReaders.AsId(solidNode.Entity[2]) is { } directionId
      && IfcGeometryReaders.TryReadDirection(graph, directionId, out var direction)
    )
    {
      extrudedDirection = direction.Normalized();
    }

    profileDefId = profileId.Value;
    depthMm = depth;
    return true;
  }

  /// <summary>Reads an <c>IfcCircleProfileDef</c>'s diameter (mm), doubling its radius attribute.</summary>
  public static bool TryReadCircleProfile(StepGraph graph, uint profileDefId, out double diameterMm)
  {
    diameterMm = 0;

    if (
      !graph.Lookup.TryGetValue(profileDefId, out var node)
      || !node.Entity.IsEntityType("IFCCIRCLEPROFILEDEF")
      || node.Entity.Count < 4
    )
    {
      return false;
    }

    // IfcCircleProfileDef(ProfileType, ProfileName, Position, Radius)
    if (!IfcGeometryReaders.TryReadNumber(node.Entity[3], out double radius))
    {
      return false;
    }

    diameterMm = radius * 2;
    return true;
  }

  /// <summary>Reads an <c>IfcRectangleProfileDef</c>'s width/height (mm).</summary>
  public static bool TryReadRectangleProfile(
    StepGraph graph,
    uint profileDefId,
    out double widthMm,
    out double heightMm
  )
  {
    widthMm = 0;
    heightMm = 0;

    if (
      !graph.Lookup.TryGetValue(profileDefId, out var node)
      || !node.Entity.IsEntityType("IFCRECTANGLEPROFILEDEF")
      || node.Entity.Count < 5
    )
    {
      return false;
    }

    // IfcRectangleProfileDef(ProfileType, ProfileName, Position, XDim, YDim)
    if (
      !IfcGeometryReaders.TryReadNumber(node.Entity[3], out double xDim)
      || !IfcGeometryReaders.TryReadNumber(node.Entity[4], out double yDim)
    )
    {
      return false;
    }

    widthMm = xDim;
    heightMm = yDim;
    return true;
  }

  /// <summary>
  /// Reads a 2D-parameterized <c>IfcProfileDef</c>'s own <c>Position</c> (attribute index 2 - the same
  /// position for both <c>IfcRectangleProfileDef</c> and <c>IfcCircleProfileDef</c>) as a placement
  /// lying flat in its parent frame's local XY plane (see <see cref="IfcPlacement.FromAxis2Placement2D"/>).
  /// Added for openings (see <c>IfcOpeningExtractor</c>) - confirmed NOT always identity in the real
  /// file (a real opening's rectangle profile was offset AND rotated relative to its extrusion), unlike
  /// every other profile Position sampled so far. Degrades to identity if unreadable, same as
  /// <see cref="TryResolveExtrudedProfile(StepGraph,uint,out uint,out double,out IfcPlacement)"/>'s own Position handling - callers that don't need this
  /// (columns/beams, whose profile is symmetric around its own center) can ignore it.
  /// </summary>
  public static IfcPlacement ReadProfilePosition(StepGraph graph, uint profileDefId)
  {
    if (!graph.Lookup.TryGetValue(profileDefId, out var node) || node.Entity.Count < 3)
    {
      return IfcPlacement.Identity;
    }

    uint? positionId = IfcGeometryReaders.AsId(node.Entity[2]);
    if (
      positionId is null
      || !graph.Lookup.TryGetValue(positionId.Value, out var posNode)
      || !posNode.Entity.IsEntityType("IFCAXIS2PLACEMENT2D")
      || posNode.Entity.Count < 1
    )
    {
      return IfcPlacement.Identity;
    }

    uint? locationId = IfcGeometryReaders.AsId(posNode.Entity[0]);
    if (locationId is null || !IfcGeometryReaders.TryReadCartesianPoint(graph, locationId.Value, out var location))
    {
      return IfcPlacement.Identity;
    }

    IfcVector3? refDirection = null;
    if (
      posNode.Entity.Count > 1
      && IfcGeometryReaders.AsId(posNode.Entity[1]) is { } refDirectionId
      && IfcGeometryReaders.TryReadDirection(graph, refDirectionId, out var direction)
    )
    {
      refDirection = direction;
    }

    return IfcPlacement.FromAxis2Placement2D(location, refDirection);
  }

  // One vertex per 2 degrees of sweep - matches IfcBoundaryExtractor's own tessellation resolution, for
  // visual consistency between the two independent boundary sources (FootPrint vs this fallback).
  private const double ARC_TESSELLATION_STEP_DEGREES = 2.0;

  /// <summary>
  /// Reads an <c>IfcArbitraryClosedProfileDef</c>'s (or <c>IfcArbitraryProfileDefWithVoids</c>'s -
  /// same <c>OuterCurve</c> attribute position, a subtype adding only a trailing <c>InnerCurves</c>
  /// list this method doesn't need) <c>OuterCurve</c> down to an ordered boundary point list, in the
  /// profile's own local 2D space (Z=0) - NOT yet transformed by <see cref="ReadProfilePosition"/> or
  /// the extrusion's own Position, same convention as every other method here. Handles THREE curve
  /// encodings, all confirmed in real files this feature was tested against: <c>IfcIndexedPolyCurve</c>
  /// (a shared 2D point list plus <c>IfcLineIndex</c>/<c>IfcArcIndex</c> segment groups - a real
  /// Tekla-authored slab's exact shape), a plain closed <c>IfcPolyline</c> (a real ArchiCAD-authored
  /// slab's exact shape), and <c>IfcCompositeCurve</c> (a real Revit-authored slab WITH openings baked
  /// directly into an <c>IfcArbitraryProfileDefWithVoids</c>, rather than via separate
  /// <c>IfcOpeningElement</c>s - delegates to <see cref="IfcBoundaryExtractor.TryReadCurveBoundary"/>,
  /// shared with the 'FootPrint' reader, since a composite curve's rounded-corner tessellation is
  /// identical either way). All three cases share the same "no separate 'FootPrint' representation at
  /// all" gap - only a <c>Body</c> extrusion over exactly this profile. Bails out on anything else (a
  /// plain <c>IfcRectangleProfileDef</c>/<c>IfcCircleProfileDef</c> already has its own dedicated
  /// reader above; a spline/ellipse OuterCurve isn't supported here, same "never guess" rule as
  /// everywhere else).
  /// </summary>
  public static bool TryReadArbitraryClosedProfileBoundary(
    StepGraph graph,
    uint profileDefId,
    out List<IfcVector3> localPoints
  )
  {
    localPoints = [];

    if (
      !graph.Lookup.TryGetValue(profileDefId, out var node)
      || !(
        node.Entity.IsEntityType("IFCARBITRARYCLOSEDPROFILEDEF")
        || node.Entity.IsEntityType("IFCARBITRARYPROFILEDEFWITHVOIDS")
      )
      || node.Entity.Count < 3
    )
    {
      return false;
    }

    // IfcArbitraryClosedProfileDef(ProfileType, ProfileName, OuterCurve)
    // IfcArbitraryProfileDefWithVoids(...same 3..., InnerCurves) - InnerCurves (the voids) are
    // deliberately ignored here: real openings are cut separately via IfcOpeningElement/
    // IfcRelVoidsElement (see RevitNativeSchemaEnricher's opening synthesis), which a real export
    // commonly ALSO emits alongside baking the same holes into this Body geometry directly - reading
    // just the outer boundary here and letting the separate opening mechanism cut the holes avoids
    // needing a second, redundant void-reading path.
    if (
      IfcGeometryReaders.AsId(node.Entity[2]) is not { } curveId
      || !graph.Lookup.TryGetValue(curveId, out var curveNode)
    )
    {
      return false;
    }

    if (curveNode.Entity.IsEntityType("IFCPOLYLINE"))
    {
      return TryReadClosedPolylineProfile(graph, curveNode.Entity, out localPoints);
    }

    if (curveNode.Entity.IsEntityType("IFCINDEXEDPOLYCURVE"))
    {
      return TryReadIndexedPolyCurveProfile(graph, curveNode.Entity, out localPoints);
    }

    return IfcBoundaryExtractor.TryReadCurveBoundary(graph, curveNode.Entity, out localPoints);
  }

  // A closed boundary IfcPolyline commonly repeats its first point as its last (same convention
  // IfcBoundaryExtractor's own FootPrint-polyline reader already strips) - stripped here so this
  // matches the "not explicitly closed" convention every boundary reader in this project follows.
  private static bool TryReadClosedPolylineProfile(StepGraph graph, StepInstance polyline, out List<IfcVector3> points)
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

    if (result.Count > 1 && Math.Abs(result[0].X - result[^1].X) < 1e-6 && Math.Abs(result[0].Y - result[^1].Y) < 1e-6)
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

  private static bool TryReadIndexedPolyCurveProfile(
    StepGraph graph,
    StepInstance indexedPolyCurve,
    out List<IfcVector3> localPoints
  )
  {
    localPoints = [];

    // IfcIndexedPolyCurve(Points, Segments, SelfIntersect)
    if (
      indexedPolyCurve.Count < 2
      || IfcGeometryReaders.AsId(indexedPolyCurve[0]) is not { } pointListId
      || !IfcGeometryReaders.TryReadCartesianPointList2D(graph, pointListId, out var points)
      || indexedPolyCurve[1] is not StepList segments
      || segments.Values.Count == 0
    )
    {
      return false;
    }

    var result = new List<IfcVector3>();
    foreach (var segmentValue in segments.Values)
    {
      if (!TryAppendIndexedSegment(segmentValue, points, result))
      {
        return false;
      }
    }

    localPoints = result;
    return true;
  }

  // Each segment is an inline STEP "defined type" instantiation (IFCLINEINDEX(...)/IFCARCINDEX(...)),
  // parsed the same way IfcBoundaryExtractor's TryReadParameterAngle handles IFCPARAMETERVALUE - a
  // StepEntity, not a #id reference. Indices are 1-based into the shared point list (see
  // TryReadCartesianPointList2D). Follows the exact same "never add a segment's own final/shared point"
  // convention as IfcBoundaryExtractor's composite-curve handling - consecutive segments always share
  // an endpoint (one segment's last index == the next segment's first index), so leaving it for the
  // NEXT segment to supply as its own start naturally closes the loop without duplicate points.
  private static bool TryAppendIndexedSegment(StepValue segmentValue, List<IfcVector3> points, List<IfcVector3> result)
  {
    if (
      segmentValue is not StepEntity segmentEntity
      || segmentEntity.Attributes.Values.Count == 0
      || segmentEntity.Attributes.Values[0] is not StepList indexList
      || indexList.Values.Count == 0
    )
    {
      return false;
    }

    var indices = new List<int>(indexList.Values.Count);
    foreach (var indexValue in indexList.Values)
    {
      if (!IfcGeometryReaders.TryReadNumber(indexValue, out double indexNumber))
      {
        return false;
      }
      indices.Add((int)indexNumber);
    }

    if (indices.Any(index => index < 1 || index > points.Count))
    {
      return false;
    }

    string segmentType = segmentEntity.EntityType.ToString();
    if (segmentType.Equals("IFCLINEINDEX", StringComparison.OrdinalIgnoreCase))
    {
      for (int i = 0; i < indices.Count - 1; i++)
      {
        result.Add(points[indices[i] - 1]);
      }
      return true;
    }

    if (segmentType.Equals("IFCARCINDEX", StringComparison.OrdinalIgnoreCase))
    {
      // IfcArcIndex is always exactly 3 indices: start, a point ON the arc (used only to fit the
      // circle and determine sweep direction - never added to the result directly), end.
      return indices.Count == 3
        && TryAppendTessellatedThreePointArc(
          points[indices[0] - 1],
          points[indices[1] - 1],
          points[indices[2] - 1],
          result
        );
    }

    // e.g. a future segment kind this project doesn't recognize yet - not guessed.
    return false;
  }

  // Unlike IfcBoundaryExtractor's arc tessellation (which has an explicit trim-angle representation to
  // read), a 3-point arc gives no separate direction flag - the circle is fit from the 3 points, and
  // the sweep direction is inferred as whichever way around the circle actually passes through the
  // middle point (the only information available to disambiguate the two possible sweeps).
  private static bool TryAppendTessellatedThreePointArc(
    IfcVector3 start,
    IfcVector3 mid,
    IfcVector3 end,
    List<IfcVector3> result
  )
  {
    if (!TryComputeCircumcenter2D(start, mid, end, out var center, out double radius))
    {
      // The 3 points are collinear - not a real arc, never guessed.
      return false;
    }

    double startAngleDegrees = Math.Atan2(start.Y - center.Y, start.X - center.X) * 180.0 / Math.PI;
    double midAngleDegrees = Math.Atan2(mid.Y - center.Y, mid.X - center.X) * 180.0 / Math.PI;
    double endAngleDegrees = Math.Atan2(end.Y - center.Y, end.X - center.X) * 180.0 / Math.PI;

    double sweepCcwDegrees = NormalizeToPositive360Degrees(endAngleDegrees - startAngleDegrees);
    double midCcwDegrees = NormalizeToPositive360Degrees(midAngleDegrees - startAngleDegrees);
    // If the mid point falls within the counter-clockwise sweep from start to end, that's the real
    // direction; otherwise the arc actually goes the other way (clockwise, a negative sweep) - the only
    // way to tell with just 3 points and no explicit direction flag.
    double sweepDegrees = midCcwDegrees <= sweepCcwDegrees ? sweepCcwDegrees : sweepCcwDegrees - 360;

    int segmentCount = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweepDegrees) / ARC_TESSELLATION_STEP_DEGREES));
    // i in [0, segmentCount) only - same convention as every other tessellated arc/boundary reader
    // here: the arc's own end point is left for whatever segment comes next to supply as its start.
    for (int i = 0; i < segmentCount; i++)
    {
      double angleDegrees = startAngleDegrees + sweepDegrees * i / segmentCount;
      double angleRadians = angleDegrees * Math.PI / 180.0;
      result.Add(
        new IfcVector3(center.X + radius * Math.Cos(angleRadians), center.Y + radius * Math.Sin(angleRadians), 0)
      );
    }

    return true;
  }

  private static double NormalizeToPositive360Degrees(double degrees)
  {
    double result = degrees % 360;
    return result < 0 ? result + 360 : result;
  }

  // Standard 2D circumcenter formula - the unique circle passing through 3 non-collinear points.
  private static bool TryComputeCircumcenter2D(
    IfcVector3 a,
    IfcVector3 b,
    IfcVector3 c,
    out IfcVector3 center,
    out double radius
  )
  {
    center = IfcVector3.Zero;
    radius = 0;

    double d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
    if (Math.Abs(d) < 1e-9)
    {
      return false;
    }

    double aSq = a.X * a.X + a.Y * a.Y;
    double bSq = b.X * b.X + b.Y * b.Y;
    double cSq = c.X * c.X + c.Y * c.Y;

    double centerX = (aSq * (b.Y - c.Y) + bSq * (c.Y - a.Y) + cSq * (a.Y - b.Y)) / d;
    double centerY = (aSq * (c.X - b.X) + bSq * (a.X - c.X) + cSq * (b.X - a.X)) / d;

    center = new IfcVector3(centerX, centerY, 0);
    radius = (center - a).Length;
    return true;
  }

  private const int MAX_BOOLEAN_UNWRAP_DEPTH = 16;

  /// <summary>
  /// Resolves an element's <c>'Body'</c> down to a vertical extrusion depth (mm), for elements whose
  /// body is a real <c>IfcExtrudedAreaSolid</c> wrapped in one or more layers of
  /// <c>IfcBooleanClippingResult</c>/<c>IfcBooleanResult</c> (e.g. a wall body-clipped for a sloped
  /// top, or a slab with void cuts) - representation types <see cref="TryResolveExtrudedProfile(StepGraph,uint,out uint,out double,out IfcPlacement)"/>
  /// deliberately doesn't unwrap (it only handles a direct <c>"SweptSolid"</c> body). Added for wall
  /// height: confirmed in the real file that a wall's <c>Body</c> is <c>"Clipping"</c>
  /// (<c>IfcBooleanClippingResult(.DIFFERENCE., baseExtrusion, clippingHalfSpace)</c>) wrapping exactly
  /// this shape, with the base extrusion's own <c>Depth</c> being the wall's real, pre-clip height.
  /// </summary>
  /// <remarks>
  /// Deliberately only reads the FIRST operand at each boolean level (the thing being subtracted
  /// FROM, never the tool doing the subtracting) - the first operand is always the "base" shape in
  /// both <c>IfcBooleanClippingResult</c> and <c>IfcBooleanResult</c>'s attribute order, so this
  /// naturally walks toward the original, unclipped extrusion. Verifies the extrusion's own
  /// <c>ExtrudedDirection</c> is (anti)parallel to +Z before trusting <c>Depth</c> as a vertical
  /// height - never guesses if the body turns out to be extruded some other way.
  /// </remarks>
  public static bool TryResolveBodyExtrusionDepth(StepGraph graph, uint elementId, out double depthMm)
  {
    depthMm = 0;

    if (!IfcRepresentationResolver.TryResolveFirstItem(graph, elementId, "Body", out var item, out _))
    {
      return false;
    }

    return IfcGeometryReaders.AsId(item) is { } itemId
      && TryResolveExtrusionDepthRecursive(graph, itemId, 0, out depthMm);
  }

  private static bool TryResolveExtrusionDepthRecursive(StepGraph graph, uint id, int depth, out double depthMm)
  {
    depthMm = 0;

    if (depth >= MAX_BOOLEAN_UNWRAP_DEPTH || !graph.Lookup.TryGetValue(id, out var node))
    {
      return false;
    }

    if (node.Entity.IsEntityType("IFCEXTRUDEDAREASOLID"))
    {
      // IfcExtrudedAreaSolid(SweptArea, Position, ExtrudedDirection, Depth)
      if (
        node.Entity.Count < 4
        || IfcGeometryReaders.AsId(node.Entity[2]) is not { } directionId
        || !IfcGeometryReaders.TryReadDirection(graph, directionId, out var extrudedDirection)
        || Math.Abs(extrudedDirection.Normalized().Z) < 0.99
        || !IfcGeometryReaders.TryReadNumber(node.Entity[3], out double depthValue)
      )
      {
        return false;
      }

      depthMm = depthValue;
      return true;
    }

    if (node.Entity.IsEntityType("IFCBOOLEANCLIPPINGRESULT") || node.Entity.IsEntityType("IFCBOOLEANRESULT"))
    {
      // IfcBooleanClippingResult/IfcBooleanResult(Operator, FirstOperand, SecondOperand) - FirstOperand
      // (attribute index 1) is always the base shape being subtracted FROM, never the cutting tool.
      return node.Entity.Count > 1
        && IfcGeometryReaders.AsId(node.Entity[1]) is { } firstOperandId
        && TryResolveExtrusionDepthRecursive(graph, firstOperandId, depth + 1, out depthMm);
    }

    // e.g. a CSG body that isn't a simple boolean-of-extrusion chain - not yet supported, never guessed.
    return false;
  }
}
