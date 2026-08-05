using Speckle.Converters.IfcShared.Geometry;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Extraction;

/// <summary>
/// Resolves an <c>IfcOpeningElement</c>'s cross-section as a world-space boundary polygon, for
/// cutting into its host (see <c>IfcOpeningHostExtractor</c> for the host relationship). Confirmed
/// against real files: an opening's <c>Body</c> is a clean <c>IfcExtrudedAreaSolid</c> - same shape
/// as columns/beams - over one of three profile shapes: <c>IfcRectangleProfileDef</c> (the common
/// case), <c>IfcCircleProfileDef</c> (e.g. a round duct/pipe penetration - tessellated the same way
/// <see cref="IfcBoundaryExtractor"/> tessellates a rounded FootPrint corner), or
/// <c>IfcArbitraryClosedProfileDef</c> (an irregular polygon opening - reuses
/// <see cref="IfcProfileExtractor.TryReadArbitraryClosedProfileBoundary"/>, the same reader a slab's
/// own Body-derived boundary uses). Anything else (a spline/ellipse profile) bails out rather than
/// guessing.
/// </summary>
/// <remarks>
/// Unlike a column's profile (symmetric around the extrusion's own center, so only the extrusion's
/// <c>Position</c> matters - see <see cref="IfcProfileExtractor"/>'s remarks), an opening's profile
/// can ALSO have its own non-identity <c>Position</c> (confirmed in a real file: offset AND rotated
/// relative to the extrusion) - so the full chain (element placement -&gt; extrusion Position -&gt;
/// profile Position -&gt; local profile boundary) must be composed, not just the first two levels.
/// </remarks>
public static class IfcOpeningExtractor
{
  // One vertex per 5 degrees - a circular opening doesn't need FootPrint-corner smoothness (2deg),
  // it's a full closed loop rather than a single rounded corner in an otherwise-straight boundary.
  private const double CIRCLE_TESSELLATION_STEP_DEGREES = 5.0;

  public static bool TryExtractBoundary(StepGraph graph, uint openingElementId, out List<IfcVector3> points)
  {
    points = [];

    if (
      !IfcProfileExtractor.TryResolveExtrudedProfile(
        graph,
        openingElementId,
        out uint profileDefId,
        out _,
        out var extrusionPosition
      )
    )
    {
      return false;
    }

    List<IfcVector3> localPoints;
    if (IfcProfileExtractor.TryReadRectangleProfile(graph, profileDefId, out double widthMm, out double heightMm))
    {
      double halfWidth = widthMm / 2;
      double halfHeight = heightMm / 2;
      localPoints =
      [
        new IfcVector3(-halfWidth, -halfHeight, 0),
        new IfcVector3(halfWidth, -halfHeight, 0),
        new IfcVector3(halfWidth, halfHeight, 0),
        new IfcVector3(-halfWidth, halfHeight, 0),
      ];
    }
    else if (IfcProfileExtractor.TryReadCircleProfile(graph, profileDefId, out double diameterMm))
    {
      localPoints = TessellateCircle(diameterMm / 2);
    }
    else if (!IfcProfileExtractor.TryReadArbitraryClosedProfileBoundary(graph, profileDefId, out localPoints))
    {
      // e.g. an ellipse/spline profile - not yet supported, never guessed.
      return false;
    }

    var profilePosition = IfcProfileExtractor.ReadProfilePosition(graph, profileDefId);

    if (
      !IfcPlacementResolver.TryResolveElementPlacement(graph, openingElementId, out var elementPlacement)
      || elementPlacement is null
    )
    {
      return false;
    }

    var combined = elementPlacement.Compose(extrusionPosition).Compose(profilePosition);

    var result = new List<IfcVector3>(localPoints.Count);
    foreach (var localPoint in localPoints)
    {
      result.Add(combined.TransformPoint(localPoint));
    }

    points = result;
    return true;
  }

  private static List<IfcVector3> TessellateCircle(double radius)
  {
    int segmentCount = Math.Max(3, (int)Math.Ceiling(360.0 / CIRCLE_TESSELLATION_STEP_DEGREES));
    var points = new List<IfcVector3>(segmentCount);
    for (int i = 0; i < segmentCount; i++)
    {
      double angleRadians = 2 * Math.PI * i / segmentCount;
      points.Add(new IfcVector3(radius * Math.Cos(angleRadians), radius * Math.Sin(angleRadians), 0));
    }
    return points;
  }
}
