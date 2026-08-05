using Speckle.Converters.IfcShared.Geometry;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Extraction;

/// <summary>
/// Small, shared readers for the handful of plain-data IFC entities (points/directions/numbers) every
/// higher-level extractor needs. Never throws - returns <c>false</c>/<c>null</c> on anything
/// unexpected, per this feature's "soft-fail everywhere, never a wrong guess" rule.
/// </summary>
public static class IfcGeometryReaders
{
  public static bool TryReadCartesianPoint(StepGraph graph, uint id, out IfcVector3 point) =>
    TryReadVectorEntity(graph, id, "IFCCARTESIANPOINT", out point);

  public static bool TryReadDirection(StepGraph graph, uint id, out IfcVector3 direction) =>
    TryReadVectorEntity(graph, id, "IFCDIRECTION", out direction);

  private static bool TryReadVectorEntity(StepGraph graph, uint id, string expectedType, out IfcVector3 result)
  {
    result = IfcVector3.Zero;

    if (!graph.Lookup.TryGetValue(id, out var node) || !node.Entity.IsEntityType(expectedType))
    {
      return false;
    }

    if (node.Entity.Count == 0 || node.Entity[0] is not StepList coords || coords.Values.Count < 2)
    {
      return false;
    }

    if (!TryReadNumber(coords.Values[0], out double x) || !TryReadNumber(coords.Values[1], out double y))
    {
      return false;
    }

    double z = 0;
    if (coords.Values.Count > 2 && !TryReadNumber(coords.Values[2], out z))
    {
      return false;
    }

    result = new IfcVector3(x, y, z);
    return true;
  }

  public static bool TryReadNumber(StepValue value, out double result)
  {
    if (value is StepNumber n)
    {
      // StepNumber.Value lazily parses its raw byte span on every access (ByteSpanExtensions.
      // ToDouble) - found from a live Tekla receive to throw FormatException uncaught on a
      // malformed/non-standard numeric literal, which propagated all the way up through
      // RevitNativeSchemaEnricher.EnrichFromFile and aborted enrichment for the ENTIRE file (every
      // object fell back to DirectShape, not just the one grid axis with the bad value). "Try"
      // means never guess AND never throw - one unparseable number must only fail its own
      // extraction, matching every other extractor in this project.
      try
      {
        result = n.Value;
        return true;
      }
      catch (FormatException)
      {
        result = 0;
        return false;
      }
      catch (OverflowException)
      {
        result = 0;
        return false;
      }
    }

    result = 0;
    return false;
  }

  /// <summary>
  /// Reads an <c>IfcCartesianPointList2D</c>'s <c>CoordList</c> - unlike every other point reference in
  /// this project, this is a LIST OF INLINE <c>[x,y]</c> NUMBER PAIRS, not a list of <c>#id</c>
  /// references to separate <c>IfcCartesianPoint</c> entities. Shared by <see cref="IfcProfileExtractor"/>
  /// (an <c>IfcIndexedPolyCurve</c> profile boundary) and <see cref="IfcAxisExtractor"/> (an
  /// <c>IfcIndexedPolyCurve</c> Axis line) - both confirmed to use this encoding in real files.
  /// </summary>
  public static bool TryReadCartesianPointList2D(StepGraph graph, uint pointListId, out List<IfcVector3> points)
  {
    points = [];

    if (
      !graph.Lookup.TryGetValue(pointListId, out var node)
      || !node.Entity.IsEntityType("IFCCARTESIANPOINTLIST2D")
      || node.Entity.Count == 0
      || node.Entity[0] is not StepList coordList
    )
    {
      return false;
    }

    var result = new List<IfcVector3>(coordList.Values.Count);
    foreach (var coordValue in coordList.Values)
    {
      if (
        coordValue is not StepList coord
        || coord.Values.Count < 2
        || !TryReadNumber(coord.Values[0], out double x)
        || !TryReadNumber(coord.Values[1], out double y)
      )
      {
        return false;
      }
      result.Add(new IfcVector3(x, y, 0));
    }

    points = result;
    return true;
  }

  /// <summary>
  /// Reads an <c>IfcCartesianPointList3D</c>'s <c>CoordList</c> (the same inline-triple encoding as
  /// <see cref="TryReadCartesianPointList2D"/>, one dimension higher) - the shared vertex list backing
  /// an <c>IfcPolygonalFaceSet</c> (IFC4's compact tessellation format, confirmed as a real
  /// ArchiCAD-exported beam's entire <c>Body</c> - no <c>AdvancedBrep</c>/<c>FacetedBrep</c> wrapper at
  /// all, no Axis representation either). Every vertex the mesh is built from is already listed here
  /// directly - reading this alone (without walking <c>IfcIndexedPolygonalFace</c> entries) is
  /// sufficient for this project's purposes (a bounding-box/lowest-vertex position anchor, never a
  /// requirement to reconstruct the actual faces/topology).
  /// </summary>
  public static bool TryReadCartesianPointList3D(StepGraph graph, uint pointListId, out List<IfcVector3> points)
  {
    points = [];

    if (
      !graph.Lookup.TryGetValue(pointListId, out var node)
      || !node.Entity.IsEntityType("IFCCARTESIANPOINTLIST3D")
      || node.Entity.Count == 0
      || node.Entity[0] is not StepList coordList
    )
    {
      return false;
    }

    var result = new List<IfcVector3>(coordList.Values.Count);
    foreach (var coordValue in coordList.Values)
    {
      if (
        coordValue is not StepList coord
        || coord.Values.Count < 3
        || !TryReadNumber(coord.Values[0], out double x)
        || !TryReadNumber(coord.Values[1], out double y)
        || !TryReadNumber(coord.Values[2], out double z)
      )
      {
        return false;
      }
      result.Add(new IfcVector3(x, y, z));
    }

    points = result;
    return true;
  }

  /// <summary>Resolves <paramref name="value"/> to an id if it's a reference, or <c>null</c> otherwise (e.g. <c>$</c>/<c>*</c>).</summary>
  public static uint? AsId(StepValue value) => value is StepId id ? id.Id : null;
}
