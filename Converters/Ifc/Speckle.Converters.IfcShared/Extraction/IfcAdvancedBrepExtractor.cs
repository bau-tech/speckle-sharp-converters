using Speckle.Converters.IfcShared.Geometry;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Extraction;

/// <summary>
/// Reads every raw vertex out of an element's <c>'Body'</c> B-Rep representation - NOT a geometry
/// kernel, just chasing STEP references down to every <c>IfcCartesianPoint</c> the shape is built from.
/// Supports THREE different Body encodings, each confirmed in a real file this feature was tested
/// against: <c>IfcAdvancedBrep -&gt; IfcClosedShell -&gt; [every] IfcAdvancedFace -&gt; [every]
/// IfcFaceBound -&gt; IfcEdgeLoop -&gt; [every] IfcOrientedEdge -&gt; IfcEdgeCurve -&gt; (IfcVertexPoint
/// start AND end) -&gt; IfcCartesianPoint</c> (a real pile); <c>IfcFacetedBrep -&gt; IfcClosedShell -&gt;
/// [every] IfcFace -&gt; [every] IfcFaceBound -&gt; IfcPolyLoop -&gt; [every point directly]</c> (a real
/// Tekla-authored precast wall panel) - a faceted brep's loop is a flat list of point references with
/// no edge/vertex indirection at all; and <c>IfcPolygonalFaceSet -&gt; Coordinates
/// (IfcCartesianPointList3D, every vertex listed directly)</c> (a real ArchiCAD-authored beam, IFC4's
/// compact tessellation format - simpler still, since every vertex the mesh is built from is already
/// in one flat list with no faces/loops to walk at all for this project's purposes).
/// </summary>
/// <remarks>
/// Used ONLY as a last-resort per-instance position anchor/shape hint for elements with no cleaner
/// data available: (1) piles whose repeated-type <c>ObjectPlacement</c> is identical across every
/// instance (confirmed in a live receive - 216 piles all sharing the exact same local placement point,
/// causing every one to land on top of the others - the same bug class as the column extrusion-Position
/// bug found earlier in this feature, except the real per-instance offset is baked into the Body's own
/// vertices rather than a profile's own Position), via <see cref="TryReadLowestBodyVertex"/>; and (2)
/// walls/beams with no <c>'Axis'</c> representation at all (confirmed for Tekla-exported precast panels
/// with openings, and separately for ArchiCAD's Reference-View-exported beams - two unrelated tools
/// dropping to a raw mesh Body for two different reasons), via a bounding-box heuristic that consumes
/// <see cref="TryReadAllBodyVertices"/>'s full point list (see <c>RevitNativeSchemaEnricher</c>'s
/// axis-with-fallbacks resolution).
/// </remarks>
public static class IfcAdvancedBrepExtractor
{
  /// <summary>
  /// Collects every vertex reachable from the element's <c>Body</c> shell, in no particular order and
  /// with duplicates (a vertex shared by multiple edges/faces is read once per edge/face that
  /// references it) - callers that need a single point (e.g. <see cref="TryReadLowestBodyVertex"/>) or
  /// a bounding box reduce this list themselves.
  /// </summary>
  public static bool TryReadAllBodyVertices(StepGraph graph, uint elementId, out List<IfcVector3> points)
  {
    points = [];

    if (!IfcRepresentationResolver.TryResolveFirstItem(graph, elementId, "Body", out var item, out _))
    {
      return false;
    }

    if (IfcGeometryReaders.AsId(item) is not { } bodyId || !graph.Lookup.TryGetValue(bodyId, out var bodyNode))
    {
      return false;
    }

    if (bodyNode.Entity.IsEntityType("IFCADVANCEDBREP"))
    {
      return TryReadVerticesFromEdgeBasedShell(graph, bodyId, points);
    }

    if (bodyNode.Entity.IsEntityType("IFCFACETEDBREP"))
    {
      return TryReadVerticesFromPolyLoopShell(graph, bodyId, points);
    }

    if (bodyNode.Entity.IsEntityType("IFCPOLYGONALFACESET"))
    {
      return TryReadVerticesFromPolygonalFaceSet(graph, bodyId, points);
    }

    // e.g. a CSG body, or a different tessellation shape (IfcTriangulatedFaceSet) - not yet supported,
    // never guessed.
    return false;
  }

  /// <summary>
  /// Collects the element's <c>Body</c> shell as a list of FACES, each an ordered, closed vertex loop
  /// (as authored - never re-ordered or stitched together by this method) - used for deriving a planar
  /// boundary from a mesh body that has no <c>'FootPrint'</c>/extrusion profile at all (see
  /// <c>IfcMeshBoundaryExtractor</c>). Supports <c>IfcFacetedBrep</c> (each <c>IfcPolyLoop</c> is
  /// already an ordered point list) and <c>IfcPolygonalFaceSet</c> (each <c>IfcIndexedPolygonalFace</c>
  /// is already an ordered point-index list) - both encodings give faces as clean, already-ordered
  /// loops directly from the STEP data, no reconstruction needed. Deliberately does NOT support
  /// <c>IfcAdvancedBrep</c>'s edge-based shell here: reconstructing an ordered loop from its
  /// <c>IfcOrientedEdge</c> list would require resolving edge connectivity and orientation, real added
  /// complexity with no confirmed real case needing it yet (piles, the only real AdvancedBrep case
  /// found so far, only ever needed a representative vertex - see
  /// <see cref="TryReadLowestBodyVertex"/> - never a face boundary).
  /// </summary>
  public static bool TryReadAllBodyFaces(StepGraph graph, uint elementId, out List<List<IfcVector3>> faces)
  {
    faces = [];

    if (!IfcRepresentationResolver.TryResolveFirstItem(graph, elementId, "Body", out var item, out _))
    {
      return false;
    }

    if (IfcGeometryReaders.AsId(item) is not { } bodyId || !graph.Lookup.TryGetValue(bodyId, out var bodyNode))
    {
      return false;
    }

    if (bodyNode.Entity.IsEntityType("IFCFACETEDBREP"))
    {
      return TryReadFacesFromPolyLoopShell(graph, bodyId, faces);
    }

    if (bodyNode.Entity.IsEntityType("IFCPOLYGONALFACESET"))
    {
      return TryReadFacesFromPolygonalFaceSet(graph, bodyId, faces);
    }

    return false;
  }

  public static bool TryReadLowestBodyVertex(StepGraph graph, uint elementId, out IfcVector3 point)
  {
    point = IfcVector3.Zero;

    if (!TryReadAllBodyVertices(graph, elementId, out var points) || points.Count == 0)
    {
      return false;
    }

    point = points[0];
    foreach (var candidate in points)
    {
      if (candidate.Z < point.Z)
      {
        point = candidate;
      }
    }

    return true;
  }

  // IfcAdvancedBrep(Outer: IfcClosedShell) -> CfsFaces: [IfcAdvancedFace] -> Bounds: [IfcFaceBound/
  // IfcFaceOuterBound] -> Bound: IfcEdgeLoop -> EdgeList: [IfcOrientedEdge] -> EdgeElement:
  // IfcEdgeCurve -> EdgeStart/EdgeEnd: IfcVertexPoint -> VertexGeometry: IfcCartesianPoint.
  private static bool TryReadVerticesFromEdgeBasedShell(StepGraph graph, uint advancedBrepId, List<IfcVector3> points)
  {
    if (
      !TryGetSingleRef(graph, advancedBrepId, "IFCADVANCEDBREP", 0, out uint closedShellId)
      || !TryGetListRefs(graph, closedShellId, "IFCCLOSEDSHELL", 0, out var faceIds)
    )
    {
      return false;
    }

    bool found = false;
    foreach (uint faceId in faceIds)
    {
      if (!TryGetListRefs(graph, faceId, "IFCADVANCEDFACE", 0, out var boundIds))
      {
        continue;
      }

      foreach (uint boundId in boundIds)
      {
        // IfcFaceOuterBound and IfcFaceBound (an inner/hole loop) share the same attribute shape
        // (Bound, Orientation) - either is fine here, we only want vertices, not to distinguish holes
        // from outer boundaries.
        if (
          !TryGetSingleRef(graph, boundId, "IFCFACEOUTERBOUND", 0, out uint edgeLoopId)
          && !TryGetSingleRef(graph, boundId, "IFCFACEBOUND", 0, out edgeLoopId)
        )
        {
          continue;
        }

        if (!TryGetListRefs(graph, edgeLoopId, "IFCEDGELOOP", 0, out var orientedEdgeIds))
        {
          continue;
        }

        foreach (uint orientedEdgeId in orientedEdgeIds)
        {
          // IfcOrientedEdge(EdgeStart=*, EdgeEnd=*, EdgeElement, Orientation) - attributes 0/1 are
          // derived placeholders (STEP-encoded as '*'), EdgeElement is attribute index 2.
          if (!TryGetSingleRef(graph, orientedEdgeId, "IFCORIENTEDEDGE", 2, out uint edgeCurveId))
          {
            continue;
          }

          // IfcEdgeCurve(EdgeStart, EdgeEnd, EdgeGeometry, SameSense) - both endpoints are candidate
          // vertices; we don't care which is "start" vs "end" for this purpose.
          if (
            TryGetSingleRef(graph, edgeCurveId, "IFCEDGECURVE", 0, out uint startVertexId)
            && TryGetSingleRef(graph, startVertexId, "IFCVERTEXPOINT", 0, out uint startPointId)
            && IfcGeometryReaders.TryReadCartesianPoint(graph, startPointId, out var startPoint)
          )
          {
            points.Add(startPoint);
            found = true;
          }

          if (
            TryGetSingleRef(graph, edgeCurveId, "IFCEDGECURVE", 1, out uint endVertexId)
            && TryGetSingleRef(graph, endVertexId, "IFCVERTEXPOINT", 0, out uint endPointId)
            && IfcGeometryReaders.TryReadCartesianPoint(graph, endPointId, out var endPoint)
          )
          {
            points.Add(endPoint);
            found = true;
          }
        }
      }
    }

    return found;
  }

  // IfcFacetedBrep(Outer: IfcClosedShell) -> CfsFaces: [IfcFace] -> Bounds: [IfcFaceBound/
  // IfcFaceOuterBound] -> Bound: IfcPolyLoop -> Polygon: [IfcCartesianPoint] directly - no edge/vertex
  // indirection at all, much simpler than the AdvancedBrep case.
  private static bool TryReadVerticesFromPolyLoopShell(StepGraph graph, uint facetedBrepId, List<IfcVector3> points)
  {
    if (
      !TryGetSingleRef(graph, facetedBrepId, "IFCFACETEDBREP", 0, out uint closedShellId)
      || !TryGetListRefs(graph, closedShellId, "IFCCLOSEDSHELL", 0, out var faceIds)
    )
    {
      return false;
    }

    bool found = false;
    foreach (uint faceId in faceIds)
    {
      if (!TryGetListRefs(graph, faceId, "IFCFACE", 0, out var boundIds))
      {
        continue;
      }

      foreach (uint boundId in boundIds)
      {
        if (
          !TryGetSingleRef(graph, boundId, "IFCFACEOUTERBOUND", 0, out uint polyLoopId)
          && !TryGetSingleRef(graph, boundId, "IFCFACEBOUND", 0, out polyLoopId)
        )
        {
          continue;
        }

        if (
          !graph.Lookup.TryGetValue(polyLoopId, out var polyLoopNode)
          || !polyLoopNode.Entity.IsEntityType("IFCPOLYLOOP")
          || polyLoopNode.Entity.Count == 0
          || polyLoopNode.Entity[0] is not StepList pointRefs
        )
        {
          continue;
        }

        foreach (var pointValue in pointRefs.Values)
        {
          if (
            IfcGeometryReaders.AsId(pointValue) is { } pointId
            && IfcGeometryReaders.TryReadCartesianPoint(graph, pointId, out var point)
          )
          {
            points.Add(point);
            found = true;
          }
        }
      }
    }

    return found;
  }

  // IfcPolygonalFaceSet(Coordinates, Closed, Faces, PnIndex) - Coordinates (an IfcCartesianPointList3D)
  // already lists every vertex the mesh is built from directly; reading it alone (without walking the
  // Faces list of IfcIndexedPolygonalFace entries) is sufficient for this project's purposes - a
  // bounding-box/lowest-vertex position anchor, never a requirement to reconstruct the actual topology.
  private static bool TryReadVerticesFromPolygonalFaceSet(StepGraph graph, uint faceSetId, List<IfcVector3> points)
  {
    if (
      !graph.Lookup.TryGetValue(faceSetId, out var node)
      || !node.Entity.IsEntityType("IFCPOLYGONALFACESET")
      || node.Entity.Count == 0
      || IfcGeometryReaders.AsId(node.Entity[0]) is not { } coordListId
      || !IfcGeometryReaders.TryReadCartesianPointList3D(graph, coordListId, out var vertices)
    )
    {
      return false;
    }

    points.AddRange(vertices);
    return vertices.Count > 0;
  }

  // IfcFacetedBrep(Outer: IfcClosedShell) -> CfsFaces: [IfcFace] -> Bounds: [IfcFaceBound/
  // IfcFaceOuterBound] -> Bound: IfcPolyLoop -> Polygon: [IfcCartesianPoint], already an ordered loop.
  // Only each face's OUTER bound is collected (a FaceBound/inner hole loop is skipped) - this method is
  // used to find a floor's overall footprint, which is the outer silhouette, not any hole cut into it.
  private static bool TryReadFacesFromPolyLoopShell(StepGraph graph, uint facetedBrepId, List<List<IfcVector3>> faces)
  {
    if (
      !TryGetSingleRef(graph, facetedBrepId, "IFCFACETEDBREP", 0, out uint closedShellId)
      || !TryGetListRefs(graph, closedShellId, "IFCCLOSEDSHELL", 0, out var faceIds)
    )
    {
      return false;
    }

    foreach (uint faceId in faceIds)
    {
      if (
        !TryGetListRefs(graph, faceId, "IFCFACE", 0, out var boundIds)
        || !TryFindOuterBound(graph, boundIds, out uint polyLoopId)
      )
      {
        continue;
      }

      if (
        !graph.Lookup.TryGetValue(polyLoopId, out var polyLoopNode)
        || !polyLoopNode.Entity.IsEntityType("IFCPOLYLOOP")
        || polyLoopNode.Entity.Count == 0
        || polyLoopNode.Entity[0] is not StepList pointRefs
      )
      {
        continue;
      }

      var face = new List<IfcVector3>(pointRefs.Values.Count);
      bool faceOk = true;
      foreach (var pointValue in pointRefs.Values)
      {
        if (
          IfcGeometryReaders.AsId(pointValue) is not { } pointId
          || !IfcGeometryReaders.TryReadCartesianPoint(graph, pointId, out var point)
        )
        {
          faceOk = false;
          break;
        }
        face.Add(point);
      }

      if (faceOk && face.Count >= 3)
      {
        faces.Add(face);
      }
    }

    return faces.Count > 0;
  }

  // IfcPolygonalFaceSet(Coordinates, Closed, Faces, PnIndex) - each IfcIndexedPolygonalFace's own
  // point-index list is already an ordered loop into the shared Coordinates list.
  private static bool TryReadFacesFromPolygonalFaceSet(StepGraph graph, uint faceSetId, List<List<IfcVector3>> faces)
  {
    if (
      !graph.Lookup.TryGetValue(faceSetId, out var node)
      || !node.Entity.IsEntityType("IFCPOLYGONALFACESET")
      || node.Entity.Count < 3
      || IfcGeometryReaders.AsId(node.Entity[0]) is not { } coordListId
      || !IfcGeometryReaders.TryReadCartesianPointList3D(graph, coordListId, out var vertices)
      || node.Entity[2] is not StepList faceList
    )
    {
      return false;
    }

    foreach (var faceValue in faceList.Values)
    {
      if (
        IfcGeometryReaders.AsId(faceValue) is not { } faceId
        || !graph.Lookup.TryGetValue(faceId, out var faceNode)
        || !faceNode.Entity.IsEntityType("IFCINDEXEDPOLYGONALFACE")
        || faceNode.Entity.Count == 0
        || faceNode.Entity[0] is not StepList indexList
      )
      {
        continue;
      }

      var face = new List<IfcVector3>(indexList.Values.Count);
      bool faceOk = true;
      foreach (var indexValue in indexList.Values)
      {
        if (!IfcGeometryReaders.TryReadNumber(indexValue, out double indexNumber))
        {
          faceOk = false;
          break;
        }
        int index = (int)indexNumber;
        if (index < 1 || index > vertices.Count)
        {
          faceOk = false;
          break;
        }
        face.Add(vertices[index - 1]);
      }

      if (faceOk && face.Count >= 3)
      {
        faces.Add(face);
      }
    }

    return faces.Count > 0;
  }

  // Prefers a bound explicitly typed IFCFACEOUTERBOUND (the schema-correct marker for a face's outer
  // loop) over blindly trusting list order - IfcFace.Bounds is a SET, not guaranteed to list the outer
  // bound first. Falls back to the first plain IFCFACEBOUND only if no outer-typed bound exists at all.
  private static bool TryFindOuterBound(StepGraph graph, List<uint> boundIds, out uint loopId)
  {
    loopId = 0;

    foreach (uint boundId in boundIds)
    {
      if (TryGetSingleRef(graph, boundId, "IFCFACEOUTERBOUND", 0, out loopId))
      {
        return true;
      }
    }

    foreach (uint boundId in boundIds)
    {
      if (TryGetSingleRef(graph, boundId, "IFCFACEBOUND", 0, out loopId))
      {
        return true;
      }
    }

    return false;
  }

  private static bool TryGetSingleRef(StepGraph graph, uint id, string expectedType, int attributeIndex, out uint refId)
  {
    refId = 0;

    if (
      !graph.Lookup.TryGetValue(id, out var node)
      || !node.Entity.IsEntityType(expectedType)
      || node.Entity.Count <= attributeIndex
      || IfcGeometryReaders.AsId(node.Entity[attributeIndex]) is not { } result
    )
    {
      return false;
    }

    refId = result;
    return true;
  }

  private static bool TryGetListRefs(
    StepGraph graph,
    uint id,
    string expectedType,
    int attributeIndex,
    out List<uint> refIds
  )
  {
    refIds = [];

    if (
      !graph.Lookup.TryGetValue(id, out var node)
      || !node.Entity.IsEntityType(expectedType)
      || node.Entity.Count <= attributeIndex
      || node.Entity[attributeIndex] is not StepList list
    )
    {
      return false;
    }

    foreach (var value in list.Values)
    {
      if (IfcGeometryReaders.AsId(value) is { } refId)
      {
        refIds.Add(refId);
      }
    }

    return refIds.Count > 0;
  }
}
