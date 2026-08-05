using Speckle.Converters.IfcShared.Geometry;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Extraction;

/// <summary>
/// Infers a plausible "axis" line for an element with no declared <c>'Axis'</c> representation at all,
/// from its <c>Body</c>'s own vertex bounding box - a HEURISTIC, not a real extraction, kept in its own
/// clearly-named class so it's never mistaken for the same trustworthiness as
/// <see cref="IfcAxisExtractor"/>'s reading of an actual declared representation.
/// </summary>
/// <remarks>
/// Added for Tekla-authored precast wall panels confirmed (in a live receive) to have NO <c>'Axis'</c>
/// representation whenever the part's cut/opening geometry makes Tekla's own Auto/SweptSolid export
/// fail (it falls back to a raw <c>Brep</c>/<c>AdvancedBrep</c> body with no axis - see
/// <see cref="IfcAdvancedBrepExtractor"/>'s remarks). Confirmed against those same real panels: Tekla's
/// own local-coordinate convention already puts a part's "length" along ONE local axis (X for beams -
/// see <c>IfcAxisExtractor</c>'s remarks; confirmed by symmetry for these panels too - every sampled
/// wall's vertices were centered on 0 in the two non-length directions), so the largest bounding-box
/// extent, centered on the midpoint of the other two, is a reasonable proxy for the real centerline.
/// This is NOT guaranteed correct for an arbitrary shape - a genuinely irregular or near-cubic Body
/// (no single dominant extent) is rejected rather than guessed at, and even a confidently-picked axis
/// here is an approximation of the Body's bounding box, not the tool's own authored centerline.
/// </remarks>
public static class IfcBodyBoundingBoxAxisHeuristic
{
  // The largest extent must beat the second-largest by at least this factor before being trusted as
  // "the" length direction - a real wall/panel's length is typically several times its height and
  // dozens of times its thickness, so this is a conservative (not overly strict) bar for rejecting a
  // near-cubic or otherwise ambiguous shape.
  private const double DOMINANT_EXTENT_MARGIN = 1.2;

  public static bool TryComputeAxisFromBodyBoundingBox(
    StepGraph graph,
    uint elementId,
    out IfcVector3 start,
    out IfcVector3 end
  )
  {
    start = IfcVector3.Zero;
    end = IfcVector3.Zero;

    if (!IfcAdvancedBrepExtractor.TryReadAllBodyVertices(graph, elementId, out var points) || points.Count < 2)
    {
      return false;
    }

    double minX = points[0].X,
      maxX = points[0].X;
    double minY = points[0].Y,
      maxY = points[0].Y;
    double minZ = points[0].Z,
      maxZ = points[0].Z;
    foreach (var point in points)
    {
      minX = Math.Min(minX, point.X);
      maxX = Math.Max(maxX, point.X);
      minY = Math.Min(minY, point.Y);
      maxY = Math.Max(maxY, point.Y);
      minZ = Math.Min(minZ, point.Z);
      maxZ = Math.Max(maxZ, point.Z);
    }

    double extentX = maxX - minX;
    double extentY = maxY - minY;
    double extentZ = maxZ - minZ;

    double midX = (minX + maxX) / 2;
    double midY = (minY + maxY) / 2;
    double midZ = (minZ + maxZ) / 2;

    // Pick whichever local axis has the largest extent, and confirm it's clearly dominant (not a
    // near-cubic/ambiguous shape) before trusting it as "the" length direction.
    if (extentX >= extentY && extentX >= extentZ)
    {
      if (!IsDominant(extentX, extentY, extentZ))
      {
        return false;
      }
      start = new IfcVector3(minX, midY, midZ);
      end = new IfcVector3(maxX, midY, midZ);
      return true;
    }

    if (extentY >= extentX && extentY >= extentZ)
    {
      if (!IsDominant(extentY, extentX, extentZ))
      {
        return false;
      }
      start = new IfcVector3(midX, minY, midZ);
      end = new IfcVector3(midX, maxY, midZ);
      return true;
    }

    if (!IsDominant(extentZ, extentX, extentY))
    {
      return false;
    }
    start = new IfcVector3(midX, midY, minZ);
    end = new IfcVector3(midX, midY, maxZ);
    return true;
  }

  private static bool IsDominant(double largest, double otherA, double otherB) =>
    largest > Math.Max(otherA, otherB) * DOMINANT_EXTENT_MARGIN;
}
