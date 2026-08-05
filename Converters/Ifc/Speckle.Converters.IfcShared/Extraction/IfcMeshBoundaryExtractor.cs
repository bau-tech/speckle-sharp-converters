using Speckle.Converters.IfcShared.Geometry;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Extraction;

/// <summary>
/// Derives a planar boundary polygon from a raw mesh <c>Body</c> (see <see cref="IfcAdvancedBrepExtractor"/>'s
/// per-face reading) for elements with no <c>'FootPrint'</c> representation AND no extrudable profile
/// at all - confirmed necessary for a real ArchiCAD-exported (Reference View) floor slab, whose only
/// geometry is a <c>Tessellation</c>/<c>IfcPolygonalFaceSet</c> Body.
/// </summary>
/// <remarks>
/// APPROACH: picks the LARGEST face whose normal is close to vertical (i.e. the face lies in a
/// near-horizontal plane) and returns its own vertex loop directly as the boundary - confirmed against
/// a real file: a simple slab's mesh is a 6-quad box (1 top + 1 bottom + 4 side faces), and the top/
/// bottom faces are each already a single clean, correctly-ordered rectangle - exactly the shape this
/// needs, no reconstruction required.
/// <para/>
/// KNOWN LIMITATION, not attempted here: this assumes each planar face of the mesh is represented as
/// ONE polygon (true for <c>IfcPolygonalFaceSet</c>/<c>IfcFacetedBrep</c> exporting a simple extrusion,
/// confirmed for the real file this was built against) - a genuinely TRIANGULATED top surface (many
/// small coplanar triangles instead of one N-gon) would cause this to return just one triangle's tiny
/// boundary instead of the true footprint. There is no cheap, reliable way to detect that case from a
/// single face's own point count, so it is not guarded against - a materially harder problem (silhouette
/// extraction from a triangle soup) that would need a real 2D boolean-union step, deliberately out of
/// scope until a real file actually demonstrates the need.
/// </remarks>
public static class IfcMeshBoundaryExtractor
{
  // A face's normal must be within this fraction of vertical (|normal.Z| / |normal|) to be considered
  // "horizontal" - loose enough to tolerate a face that isn't perfectly axis-aligned (e.g. a slightly
  // sloped slab), strict enough to reject a genuinely vertical (side) face outright.
  private const double HORIZONTAL_NORMAL_THRESHOLD = 0.9;

  public static bool TryExtractLargestHorizontalFaceBoundary(
    StepGraph graph,
    uint elementId,
    out List<IfcVector3> boundaryPoints
  )
  {
    boundaryPoints = [];

    if (!IfcAdvancedBrepExtractor.TryReadAllBodyFaces(graph, elementId, out var faces) || faces.Count == 0)
    {
      return false;
    }

    List<IfcVector3>? bestFace = null;
    double bestArea = 0;
    foreach (var face in faces)
    {
      if (!TryComputeNewellNormal(face, out var normal))
      {
        continue;
      }

      var normalized = normal.Normalized();
      if (Math.Abs(normalized.Z) < HORIZONTAL_NORMAL_THRESHOLD)
      {
        // Not horizontal enough - e.g. a side face of the mesh.
        continue;
      }

      double area = ComputeHorizontalPolygonArea(face);
      if (area > bestArea)
      {
        bestArea = area;
        bestFace = face;
      }
    }

    if (bestFace is null)
    {
      return false;
    }

    boundaryPoints = bestFace;
    return true;
  }

  // Newell's method: computes a (non-unit) normal for a planar polygon of ANY vertex count, without
  // needing to pick two non-collinear edges (robust to a degenerate first edge, unlike a simple cross
  // product of the first two edges) - standard technique for exactly this "is this mesh face
  // horizontal" question.
  private static bool TryComputeNewellNormal(List<IfcVector3> face, out IfcVector3 normal)
  {
    normal = IfcVector3.Zero;

    if (face.Count < 3)
    {
      return false;
    }

    double nx = 0,
      ny = 0,
      nz = 0;
    for (int i = 0; i < face.Count; i++)
    {
      var current = face[i];
      var next = face[(i + 1) % face.Count];
      nx += (current.Y - next.Y) * (current.Z + next.Z);
      ny += (current.Z - next.Z) * (current.X + next.X);
      nz += (current.X - next.X) * (current.Y + next.Y);
    }

    normal = new IfcVector3(nx, ny, nz);
    return normal.Length > 1e-9;
  }

  // Standard 2D shoelace formula, applied directly to (X,Y) - valid here specifically because the face
  // has already been confirmed near-horizontal, so its own X/Y extent already closely approximates its
  // true planar area (no need to project into the face's own local 2D frame first).
  private static double ComputeHorizontalPolygonArea(List<IfcVector3> face)
  {
    double sum = 0;
    for (int i = 0; i < face.Count; i++)
    {
      var current = face[i];
      var next = face[(i + 1) % face.Count];
      sum += current.X * next.Y - next.X * current.Y;
    }

    return Math.Abs(sum) / 2.0;
  }
}
