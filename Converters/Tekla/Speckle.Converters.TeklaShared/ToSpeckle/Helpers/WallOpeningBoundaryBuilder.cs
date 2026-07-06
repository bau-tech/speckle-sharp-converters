namespace Speckle.Converters.TeklaShared.ToSpeckle.Helpers;

/// <summary>
/// Builds a flat rectangular boundary for a wall-panel boolean-cut opening from its cutter part's own
/// display mesh - used so a Tekla-authored window/door void can be reconstructed as a real
/// <c>DB.Opening</c> on receive (see <c>OpeningToHostConverter.CreateHostedOpening</c>, which reduces
/// any boundary curve to a world-space bounding box for a Wall host anyway, so an approximate flat
/// rectangle here is exactly as good as a precise polygon).
/// </summary>
public static class WallOpeningBoundaryBuilder
{
  /// <summary>
  /// A window/door cutter is deliberately made thicker than the host wall along the through-wall axis
  /// to guarantee full penetration (mirrors <c>RevitOpeningToBooleanPartConverter</c>'s own
  /// DEFAULT_CUT_PLATE_THICKNESS_MM for the reverse direction) - so the cutter's bounding box axis with
  /// the smallest extent is the thickness/through axis; the other two are the opening's real
  /// width/height footprint. Drops that axis, holding it at the box's midpoint, and returns the
  /// remaining footprint as a closed rectangle. Returns null if the meshes carry no vertices at all.
  /// </summary>
  public static SOG.Polyline? TryBuildRectangularBoundary(IEnumerable<SOG.Mesh> meshes, string units)
  {
    double minX = double.MaxValue;
    double minY = double.MaxValue;
    double minZ = double.MaxValue;
    double maxX = double.MinValue;
    double maxY = double.MinValue;
    double maxZ = double.MinValue;
    bool any = false;

    foreach (SOG.Mesh mesh in meshes)
    {
      for (int i = 0; i + 2 < mesh.vertices.Count; i += 3)
      {
        any = true;
        minX = Math.Min(minX, mesh.vertices[i]);
        maxX = Math.Max(maxX, mesh.vertices[i]);
        minY = Math.Min(minY, mesh.vertices[i + 1]);
        maxY = Math.Max(maxY, mesh.vertices[i + 1]);
        minZ = Math.Min(minZ, mesh.vertices[i + 2]);
        maxZ = Math.Max(maxZ, mesh.vertices[i + 2]);
      }
    }

    if (!any)
    {
      return null;
    }

    double dx = maxX - minX;
    double dy = maxY - minY;
    double dz = maxZ - minZ;

    (double X, double Y, double Z)[] corners;
    if (dx <= dy && dx <= dz)
    {
      double midX = (minX + maxX) / 2;
      corners =
      [
        (midX, minY, minZ),
        (midX, maxY, minZ),
        (midX, maxY, maxZ),
        (midX, minY, maxZ),
      ];
    }
    else if (dy <= dx && dy <= dz)
    {
      double midY = (minY + maxY) / 2;
      corners =
      [
        (minX, midY, minZ),
        (maxX, midY, minZ),
        (maxX, midY, maxZ),
        (minX, midY, maxZ),
      ];
    }
    else
    {
      double midZ = (minZ + maxZ) / 2;
      corners =
      [
        (minX, minY, midZ),
        (maxX, minY, midZ),
        (maxX, maxY, midZ),
        (minX, maxY, midZ),
      ];
    }

    List<double> value = new(12);
    foreach (var (x, y, z) in corners)
    {
      value.Add(x);
      value.Add(y);
      value.Add(z);
    }

    return new SOG.Polyline { value = value, closed = true, units = units };
  }
}
