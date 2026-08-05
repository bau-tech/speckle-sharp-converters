using Speckle.Converters.IfcShared.Geometry;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Extraction;

/// <summary>
/// Resolves an <c>IfcLocalPlacement</c>'s absolute (world-space) placement by walking its
/// <c>PlacementRelTo</c> chain up to the root and composing each level's <c>IfcAxis2Placement3D</c>
/// via <see cref="IfcPlacement.Compose"/>. This is this feature's flagged highest-risk piece
/// (composing the chain incorrectly yields plausible-but-wrong geometry) - kept as a small, pure,
/// independently-unit-testable module for exactly that reason.
/// </summary>
public static class IfcPlacementResolver
{
  private const int MAX_DEPTH = 64; // guards against a malformed/cyclic PlacementRelTo chain

  /// <summary>
  /// Resolves the absolute placement for the <c>IfcLocalPlacement</c> at <paramref name="localPlacementId"/>.
  /// </summary>
  /// <returns><c>false</c> on anything unexpected (wrong entity type, missing reference, 2D-only
  /// placement, chain too deep) - never throws.</returns>
  public static bool TryResolveAbsolutePlacement(StepGraph graph, uint localPlacementId, out IfcPlacement? result) =>
    TryResolveRecursive(graph, localPlacementId, 0, out result);

  // IfcProduct's ObjectPlacement attribute is at a fixed index for every IfcRoot-derived product
  // (GlobalId, OwnerHistory, Name, Description, ObjectType, ObjectPlacement, Representation, ...) -
  // same schema position IfcProfileExtractor's Representation index (6) is relative to.
  private const int OBJECT_PLACEMENT_ATTRIBUTE_INDEX = 5;

  /// <summary>
  /// Convenience wrapper: resolves the absolute placement for an element (e.g. an <c>IfcColumn</c>)
  /// directly, reading its own <c>ObjectPlacement</c> attribute first instead of requiring the caller
  /// to already know the <c>IfcLocalPlacement</c> express id.
  /// </summary>
  public static bool TryResolveElementPlacement(StepGraph graph, uint elementId, out IfcPlacement? result)
  {
    result = null;

    if (
      !graph.Lookup.TryGetValue(elementId, out var elementNode)
      || elementNode.Entity.Count <= OBJECT_PLACEMENT_ATTRIBUTE_INDEX
    )
    {
      return false;
    }

    uint? placementId = IfcGeometryReaders.AsId(elementNode.Entity[OBJECT_PLACEMENT_ATTRIBUTE_INDEX]);
    return placementId is not null && TryResolveAbsolutePlacement(graph, placementId.Value, out result);
  }

  private static bool TryResolveRecursive(StepGraph graph, uint localPlacementId, int depth, out IfcPlacement? result)
  {
    result = null;
    if (depth >= MAX_DEPTH)
    {
      return false;
    }

    if (
      !graph.Lookup.TryGetValue(localPlacementId, out var placementNode)
      || !placementNode.Entity.IsEntityType("IFCLOCALPLACEMENT")
      || placementNode.Entity.Count < 2
    )
    {
      return false;
    }

    // IfcLocalPlacement(PlacementRelTo, RelativePlacement)
    if (!TryResolveAxis2Placement3D(graph, placementNode.Entity[1], out var local) || local is null)
    {
      return false;
    }

    uint? parentId = IfcGeometryReaders.AsId(placementNode.Entity[0]);
    if (parentId is null)
    {
      // No PlacementRelTo - this level's placement is already absolute (relative to the world/site).
      result = local;
      return true;
    }

    if (!TryResolveRecursive(graph, parentId.Value, depth + 1, out var parentAbsolute) || parentAbsolute is null)
    {
      return false;
    }

    result = parentAbsolute.Compose(local);
    return true;
  }

  private static bool TryResolveAxis2Placement3D(
    StepGraph graph,
    StepValue relativePlacementValue,
    out IfcPlacement? result
  )
  {
    result = null;
    uint? id = IfcGeometryReaders.AsId(relativePlacementValue);
    return id is not null && TryResolveAxis2Placement3DById(graph, id.Value, out result);
  }

  /// <summary>
  /// Resolves a standalone <c>IfcAxis2Placement3D</c> by its express id - e.g. an
  /// <c>IfcExtrudedAreaSolid</c>'s own <c>Position</c> attribute (see
  /// <see cref="IfcProfileExtractor.TryResolveExtrudedProfile(StepGraph,uint,out uint,out double,out IfcPlacement)"/>'s remarks: for at least one
  /// real-world export, this local frame - not the element's <c>ObjectPlacement</c> - is where the
  /// element's actual per-instance world offset lives, confirmed by a live receive where every
  /// instance of a repeated column type landed at the exact same position because only
  /// <c>ObjectPlacement</c> was being read). Public so callers outside the <c>ObjectPlacement</c>
  /// chain (like that one) can reuse the same Gram-Schmidt-via-<see cref="IfcPlacement.FromAxis2Placement3D"/>
  /// logic instead of duplicating it.
  /// </summary>
  public static bool TryResolveAxis2Placement3DById(StepGraph graph, uint axis2Placement3DId, out IfcPlacement? result)
  {
    result = null;

    if (
      !graph.Lookup.TryGetValue(axis2Placement3DId, out var node)
      || !node.Entity.IsEntityType("IFCAXIS2PLACEMENT3D")
      || node.Entity.Count < 1
    )
    {
      return false;
    }

    // IfcAxis2Placement3D(Location, Axis, RefDirection) - Axis/RefDirection are optional ($ = default).
    uint? locationId = IfcGeometryReaders.AsId(node.Entity[0]);
    if (locationId is null || !IfcGeometryReaders.TryReadCartesianPoint(graph, locationId.Value, out var location))
    {
      return false;
    }

    IfcVector3? axis = TryReadOptionalDirection(graph, node.Entity, 1);
    IfcVector3? refDirection = TryReadOptionalDirection(graph, node.Entity, 2);

    result = IfcPlacement.FromAxis2Placement3D(location, axis, refDirection);
    return true;
  }

  private static IfcVector3? TryReadOptionalDirection(StepGraph graph, StepInstance instance, int attributeIndex)
  {
    if (instance.Count <= attributeIndex)
    {
      return null;
    }

    uint? id = IfcGeometryReaders.AsId(instance[attributeIndex]);
    if (id is null)
    {
      return null;
    }

    return IfcGeometryReaders.TryReadDirection(graph, id.Value, out var direction) ? direction : null;
  }
}
