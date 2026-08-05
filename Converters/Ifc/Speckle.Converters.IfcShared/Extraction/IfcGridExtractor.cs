using Speckle.Converters.IfcShared.Geometry;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Extraction;

/// <summary>
/// Resolves an <c>IfcGrid</c>'s <c>UAxes</c>/<c>VAxes</c> (attribute indices 7/8 - fixed IFC4 schema
/// order: GlobalId, OwnerHistory, Name, Description, ObjectType, ObjectPlacement, Representation,
/// UAxes, VAxes, WAxes, PredefinedType) down to a flat list of named 2-point axis lines, in the grid's
/// own LOCAL coordinates - same convention as every other extractor in this project (callers transform
/// through <see cref="IfcPlacementResolver.TryResolveElementPlacement"/> for world coordinates).
/// <c>WAxes</c> (vertical/elevation-tier grids) is deliberately out of scope - Revit's <c>DB.Grid</c> is
/// a plan-view element with no vertical-axis concept to map it to.
/// </summary>
public static class IfcGridExtractor
{
  private const int GRID_UAXES_ATTRIBUTE_INDEX = 7;
  private const int GRID_VAXES_ATTRIBUTE_INDEX = 8;

  public readonly record struct GridAxis(uint ExpressId, string Tag, IfcVector3 Start, IfcVector3 End);

  public static bool TryExtractAxes(StepGraph graph, uint gridExpressId, out List<GridAxis> axes)
  {
    axes = [];

    if (!graph.Lookup.TryGetValue(gridExpressId, out var node) || node.Entity.Count <= GRID_VAXES_ATTRIBUTE_INDEX)
    {
      return false;
    }

    AppendAxes(graph, node.Entity[GRID_UAXES_ATTRIBUTE_INDEX], axes);
    AppendAxes(graph, node.Entity[GRID_VAXES_ATTRIBUTE_INDEX], axes);

    return axes.Count > 0;
  }

  private static void AppendAxes(StepGraph graph, StepValue axesValue, List<GridAxis> axes)
  {
    if (axesValue is not StepList axisList)
    {
      return;
    }

    foreach (var item in axisList.Values)
    {
      if (IfcGeometryReaders.AsId(item) is { } axisExpressId && TryReadAxis(graph, axisExpressId, out var axis))
      {
        axes.Add(axis!.Value);
      }
    }
  }

  // IfcGridAxis(AxisTag, AxisCurve, SameSense) - SameSense is deliberately ignored: Revit's
  // DB.Grid.Create has no direction/reversed concept, so an axis's start/end order doesn't matter here
  // the way it does for e.g. a wall's Axis (which never needs SameSense either, for the same reason).
  private static bool TryReadAxis(StepGraph graph, uint axisExpressId, out GridAxis? axis)
  {
    axis = null;

    if (
      !graph.Lookup.TryGetValue(axisExpressId, out var node)
      || node.Entity.Count < 2
      || node.Entity[0] is not StepString tagValue
      || IfcGeometryReaders.AsId(node.Entity[1]) is not { } curveId
      || !TryReadTwoPointPolyline(graph, curveId, out var start, out var end)
    )
    {
      return false;
    }

    axis = new GridAxis(axisExpressId, tagValue.Value.ToString(), start, end);
    return true;
  }

  // Only the simple 2-point IfcPolyline case is supported (every axis sampled from a live receive used
  // this form) - a multi-segment or curved grid axis is left unhandled rather than guessed at.
  private static bool TryReadTwoPointPolyline(StepGraph graph, uint curveId, out IfcVector3 start, out IfcVector3 end)
  {
    start = IfcVector3.Zero;
    end = IfcVector3.Zero;

    if (
      !graph.Lookup.TryGetValue(curveId, out var node)
      || !node.Entity.IsEntityType("IFCPOLYLINE")
      || node.Entity.Count < 1
      || node.Entity[0] is not StepList points
      || points.Values.Count != 2
    )
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
}
