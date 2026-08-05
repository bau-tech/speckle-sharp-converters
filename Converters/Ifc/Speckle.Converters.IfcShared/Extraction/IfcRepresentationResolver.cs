using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Extraction;

/// <summary>
/// Shared "find this element's representation by identifier, then resolve its first item, following
/// one level of <c>MappedRepresentation</c> indirection if present" logic - the same pattern needed
/// for both an element's <c>'Body'</c> (see <see cref="IfcProfileExtractor"/>) and its <c>'Axis'</c>
/// (see <c>IfcAxisExtractor</c>), factored out to avoid duplicating the <c>IfcMappedItem</c>/
/// <c>IfcRepresentationMap</c> indirection-following logic a second time.
/// </summary>
public static class IfcRepresentationResolver
{
  // IfcProduct's Representation attribute is at a fixed index for every IfcRoot-derived product
  // (GlobalId, OwnerHistory, Name, Description, ObjectType, ObjectPlacement, Representation, ...).
  private const int REPRESENTATION_ATTRIBUTE_INDEX = 6;

  /// <summary>
  /// Resolves <paramref name="elementId"/>'s representation identified by
  /// <paramref name="representationIdentifier"/> (e.g. <c>"Body"</c> or <c>"Axis"</c>) down to its
  /// first item and that item's own <c>RepresentationType</c> string (e.g. <c>"SweptSolid"</c>,
  /// <c>"Curve2D"</c>, <c>"Curve3D"</c>, <c>"MappedRepresentation"</c>) - following one level of
  /// <c>IfcMappedItem</c>/<c>IfcRepresentationMap</c> indirection automatically if the representation
  /// type is <c>"MappedRepresentation"</c>, so callers never need to handle that case themselves.
  /// </summary>
  public static bool TryResolveFirstItem(
    StepGraph graph,
    uint elementId,
    string representationIdentifier,
    out StepValue item,
    out string representationType
  )
  {
    item = StepUnassigned.Default;
    representationType = "";

    if (!TryGetShapeRepresentationByIdentifier(graph, elementId, representationIdentifier, out uint repId))
    {
      return false;
    }

    if (!TryGetShapeRepresentationItem(graph, repId, out var repInstance, out var itemValue))
    {
      return false;
    }

    string type = repInstance!.Count > 2 && repInstance[2] is StepString typeStr ? typeStr.Value.ToString() : "";

    if (type == "MappedRepresentation")
    {
      if (
        !TryFollowMappedItem(graph, itemValue, out uint mappedRepId)
        || !TryGetShapeRepresentationItem(graph, mappedRepId, out var mappedInstance, out var mappedItemValue)
      )
      {
        return false;
      }

      item = mappedItemValue;
      representationType =
        mappedInstance!.Count > 2 && mappedInstance[2] is StepString mappedTypeStr
          ? mappedTypeStr.Value.ToString()
          : "";
      return true;
    }

    item = itemValue;
    representationType = type;
    return true;
  }

  private static bool TryGetShapeRepresentationByIdentifier(
    StepGraph graph,
    uint elementId,
    string representationIdentifier,
    out uint repId
  )
  {
    repId = 0;

    if (
      !graph.Lookup.TryGetValue(elementId, out var elementNode)
      || elementNode.Entity.Count <= REPRESENTATION_ATTRIBUTE_INDEX
    )
    {
      return false;
    }

    uint? productDefShapeId = IfcGeometryReaders.AsId(elementNode.Entity[REPRESENTATION_ATTRIBUTE_INDEX]);
    if (
      productDefShapeId is null
      || !graph.Lookup.TryGetValue(productDefShapeId.Value, out var shapeNode)
      || !shapeNode.Entity.IsEntityType("IFCPRODUCTDEFINITIONSHAPE")
      || shapeNode.Entity.Count < 3
    )
    {
      return false;
    }

    // IfcProductDefinitionShape(Name, Description, Representations)
    if (shapeNode.Entity[2] is not StepList representations)
    {
      return false;
    }

    foreach (var repValue in representations.Values)
    {
      uint? candidateId = IfcGeometryReaders.AsId(repValue);
      if (
        candidateId is not null
        && graph.Lookup.TryGetValue(candidateId.Value, out var repNode)
        && repNode.Entity.Count > 1
        && repNode.Entity[1] is StepString identifier
        && identifier.Value.ToString() == representationIdentifier
      )
      {
        repId = candidateId.Value;
        return true;
      }
    }

    return false;
  }

  private static bool TryGetShapeRepresentationItem(
    StepGraph graph,
    uint shapeRepresentationId,
    out StepInstance? instance,
    out StepValue item
  )
  {
    instance = null;
    item = StepUnassigned.Default;

    if (
      !graph.Lookup.TryGetValue(shapeRepresentationId, out var node)
      || !node.Entity.IsEntityType("IFCSHAPEREPRESENTATION")
      || node.Entity.Count < 4
    )
    {
      return false;
    }

    // IfcShapeRepresentation(ContextOfItems, RepresentationIdentifier, RepresentationType, Items)
    if (node.Entity[3] is not StepList items || items.Values.Count == 0)
    {
      return false;
    }

    instance = node.Entity;
    item = items.Values[0];
    return true;
  }

  private static bool TryFollowMappedItem(StepGraph graph, StepValue mappedItemValue, out uint mappedRepId)
  {
    mappedRepId = 0;

    uint? mappedItemId = IfcGeometryReaders.AsId(mappedItemValue);
    if (
      mappedItemId is null
      || !graph.Lookup.TryGetValue(mappedItemId.Value, out var mappedItemNode)
      || !mappedItemNode.Entity.IsEntityType("IFCMAPPEDITEM")
      || mappedItemNode.Entity.Count < 1
    )
    {
      return false;
    }

    // IfcMappedItem(MappingSource, MappingTarget) - MappingTarget intentionally ignored: it only
    // affects where the mapped instance sits in space, not the shape it references. World placement
    // comes from the element's own ObjectPlacement (see IfcPlacementResolver) instead.
    uint? repMapId = IfcGeometryReaders.AsId(mappedItemNode.Entity[0]);
    if (
      repMapId is null
      || !graph.Lookup.TryGetValue(repMapId.Value, out var repMapNode)
      || !repMapNode.Entity.IsEntityType("IFCREPRESENTATIONMAP")
      || repMapNode.Entity.Count < 2
    )
    {
      return false;
    }

    // IfcRepresentationMap(MappingOrigin, MappedRepresentation)
    uint? mappedRepresentationId = IfcGeometryReaders.AsId(repMapNode.Entity[1]);
    if (mappedRepresentationId is null)
    {
      return false;
    }

    mappedRepId = mappedRepresentationId.Value;
    return true;
  }
}
