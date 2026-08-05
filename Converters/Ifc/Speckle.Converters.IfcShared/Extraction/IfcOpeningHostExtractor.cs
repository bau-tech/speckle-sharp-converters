using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Extraction;

/// <summary>
/// Resolves which element an <c>IfcOpeningElement</c> voids, via the <c>IfcRelVoidsElement</c>
/// relationship. Same inverse-relationship shape as <see cref="IfcMaterialThicknessExtractor"/> (the
/// relationship entity lists the host and the opening directly, rather than either one pointing back
/// to the relationship) - a one-time reverse scan, built once per receive and reused for every
/// opening, keyed by the OPENING's id (unlike the material index, which is keyed by the
/// host/element's id) since each opening has exactly one host.
/// </summary>
public static class IfcOpeningHostExtractor
{
  // IfcRelVoidsElement(GlobalId, OwnerHistory, Name, Description, RelatingBuildingElement, RelatedOpeningElement)
  private const int RELATING_BUILDING_ELEMENT_ATTRIBUTE_INDEX = 4;
  private const int RELATED_OPENING_ELEMENT_ATTRIBUTE_INDEX = 5;

  /// <summary>Scans every <c>IfcRelVoidsElement</c> once, returning a lookup from opening express id to its host element's express id.</summary>
  public static IReadOnlyDictionary<uint, uint> BuildOpeningToHostIndex(StepGraph graph)
  {
    var index = new Dictionary<uint, uint>();

    foreach (var node in graph.Nodes)
    {
      if (
        !node.Entity.IsEntityType("IFCRELVOIDSELEMENT")
        || node.Entity.Count <= RELATED_OPENING_ELEMENT_ATTRIBUTE_INDEX
      )
      {
        continue;
      }

      uint? hostId = IfcGeometryReaders.AsId(node.Entity[RELATING_BUILDING_ELEMENT_ATTRIBUTE_INDEX]);
      uint? openingId = IfcGeometryReaders.AsId(node.Entity[RELATED_OPENING_ELEMENT_ATTRIBUTE_INDEX]);
      if (hostId is not null && openingId is not null)
      {
        index[openingId.Value] = hostId.Value;
      }
    }

    return index;
  }
}
