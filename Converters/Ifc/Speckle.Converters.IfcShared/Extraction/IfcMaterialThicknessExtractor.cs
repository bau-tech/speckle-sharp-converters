using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Extraction;

/// <summary>
/// Resolves an element's total material-layer thickness (e.g. a wall's or floor's cross-section
/// depth) via the <c>IfcRelAssociatesMaterial</c> relationship. Unlike every other extractor in this
/// project, this is an INVERSE lookup - <c>IfcRelAssociatesMaterial</c> is a relationship entity that
/// lists which elements it relates to (<c>RelatedObjects</c>), not something an element points to
/// directly - so it needs a one-time reverse scan of the whole file (<see cref="BuildElementToMaterialIndex"/>),
/// built once per receive and reused for every wall/floor, rather than a per-element attribute walk.
/// </summary>
/// <remarks>
/// Confirmed against the real file (<c>rstadvancedsampleproject.ifc</c>): both a wall and a floor's
/// <c>RelatingMaterial</c> resolve to an <c>IfcMaterialLayerSetUsage</c> wrapping an
/// <c>IfcMaterialLayerSet</c> with one or more <c>IfcMaterialLayer</c>s, whose <c>LayerThickness</c>
/// sums to exactly the value already visible in the element's own name (e.g. "300mm Concrete"). A
/// plain <c>IfcMaterial</c> (single material, no layers - confirmed for the round column's material
/// association) has no thickness data at all and is correctly rejected, never guessed.
/// </remarks>
public static class IfcMaterialThicknessExtractor
{
  // IfcRelAssociatesMaterial(GlobalId, OwnerHistory, Name, Description, RelatedObjects, RelatingMaterial)
  private const int RELATED_OBJECTS_ATTRIBUTE_INDEX = 4;
  private const int RELATING_MATERIAL_ATTRIBUTE_INDEX = 5;

  /// <summary>
  /// Scans every <c>IfcRelAssociatesMaterial</c> in the file once, returning a lookup from each
  /// related element's express id to its associated material entity's express id (an
  /// <c>IfcMaterialLayerSetUsage</c>, <c>IfcMaterialLayerSet</c>, or plain <c>IfcMaterial</c> -
  /// <see cref="TryGetLayerSetThicknessMm"/> handles distinguishing them).
  /// </summary>
  public static IReadOnlyDictionary<uint, uint> BuildElementToMaterialIndex(StepGraph graph)
  {
    var index = new Dictionary<uint, uint>();

    foreach (var node in graph.Nodes)
    {
      if (
        !node.Entity.IsEntityType("IFCRELASSOCIATESMATERIAL")
        || node.Entity.Count <= RELATING_MATERIAL_ATTRIBUTE_INDEX
        || node.Entity[RELATED_OBJECTS_ATTRIBUTE_INDEX] is not StepList relatedObjects
      )
      {
        continue;
      }

      uint? materialId = IfcGeometryReaders.AsId(node.Entity[RELATING_MATERIAL_ATTRIBUTE_INDEX]);
      if (materialId is null)
      {
        continue;
      }

      foreach (var relatedObjectValue in relatedObjects.Values)
      {
        if (IfcGeometryReaders.AsId(relatedObjectValue) is { } relatedObjectId)
        {
          index[relatedObjectId] = materialId.Value;
        }
      }
    }

    return index;
  }

  /// <summary>
  /// Resolves <paramref name="elementId"/>'s total material-layer thickness (mm), via
  /// <paramref name="elementToMaterial"/> (see <see cref="BuildElementToMaterialIndex"/>). Handles the
  /// material entity being either an <c>IfcMaterialLayerSetUsage</c> (unwraps to its
  /// <c>IfcMaterialLayerSet</c>) or a bare <c>IfcMaterialLayerSet</c> directly. Returns <c>false</c>
  /// (never guesses) if there's no material association, or it's a plain single <c>IfcMaterial</c>
  /// with no layer thickness data at all.
  /// </summary>
  public static bool TryGetLayerSetThicknessMm(
    StepGraph graph,
    IReadOnlyDictionary<uint, uint> elementToMaterial,
    uint elementId,
    out double thicknessMm
  )
  {
    thicknessMm = 0;

    if (
      !elementToMaterial.TryGetValue(elementId, out uint materialId)
      || !graph.Lookup.TryGetValue(materialId, out var materialNode)
    )
    {
      return false;
    }

    uint layerSetId;
    if (materialNode.Entity.IsEntityType("IFCMATERIALLAYERSETUSAGE"))
    {
      // IfcMaterialLayerSetUsage(ForLayerSet, LayerSetDirection, DirectionSense, OffsetFromReferenceLine, ...)
      uint? forLayerSetId = materialNode.Entity.Count > 0 ? IfcGeometryReaders.AsId(materialNode.Entity[0]) : null;
      if (forLayerSetId is null)
      {
        return false;
      }
      layerSetId = forLayerSetId.Value;
    }
    else if (materialNode.Entity.IsEntityType("IFCMATERIALLAYERSET"))
    {
      layerSetId = materialId;
    }
    else
    {
      // e.g. a plain IfcMaterial (no layers - confirmed for the round column's material association)
      // or an unrecognized material entity - nothing to sum, never guess a thickness.
      return false;
    }

    if (
      !graph.Lookup.TryGetValue(layerSetId, out var layerSetNode)
      || !layerSetNode.Entity.IsEntityType("IFCMATERIALLAYERSET")
      || layerSetNode.Entity.Count == 0
      || layerSetNode.Entity[0] is not StepList materialLayers
      || materialLayers.Values.Count == 0
    )
    {
      return false;
    }

    double sum = 0;
    foreach (var layerValue in materialLayers.Values)
    {
      if (
        IfcGeometryReaders.AsId(layerValue) is not { } layerId
        || !graph.Lookup.TryGetValue(layerId, out var layerNode)
        || !layerNode.Entity.IsEntityType("IFCMATERIALLAYER")
        || layerNode.Entity.Count < 2
        || !IfcGeometryReaders.TryReadNumber(layerNode.Entity[1], out double layerThickness)
      )
      {
        // A single unreadable layer invalidates the whole sum rather than silently under-reporting.
        return false;
      }

      sum += layerThickness;
    }

    thicknessMm = sum;
    return true;
  }

  /// <summary>
  /// Resolves <paramref name="elementId"/>'s associated material NAME (e.g. "Ortbeton - bewehrt
  /// Verputzt", a generic German concrete description with no specific grade), via
  /// <paramref name="elementToMaterial"/>. Handles a plain <c>IfcMaterial</c> directly, or an
  /// <c>IfcMaterialProfileSetUsage</c> (the shape confirmed for beams/columns - unwraps to its
  /// <c>IfcMaterialProfileSet</c>'s first <c>IfcMaterialProfile</c>'s own <c>Material</c> reference) -
  /// mirrors <see cref="TryGetLayerSetThicknessMm"/>'s <c>IfcMaterialLayerSetUsage</c> unwrapping for
  /// walls/floors, just for the profile-based material association shape instead. Never guesses: an
  /// unrecognized material entity, or a name that resolves empty, returns <c>false</c>.
  /// </summary>
  public static bool TryGetMaterialName(
    StepGraph graph,
    IReadOnlyDictionary<uint, uint> elementToMaterial,
    uint elementId,
    out string? materialName
  )
  {
    materialName = null;

    if (
      !elementToMaterial.TryGetValue(elementId, out uint materialId)
      || !graph.Lookup.TryGetValue(materialId, out var materialNode)
    )
    {
      return false;
    }

    uint actualMaterialId;
    if (materialNode.Entity.IsEntityType("IFCMATERIAL"))
    {
      actualMaterialId = materialId;
    }
    else if (materialNode.Entity.IsEntityType("IFCMATERIALPROFILESETUSAGE"))
    {
      // IfcMaterialProfileSetUsage(ForProfileSet, CardinalPoint, ReferenceExtent)
      if (
        materialNode.Entity.Count == 0
        || IfcGeometryReaders.AsId(materialNode.Entity[0]) is not { } profileSetId
        || !graph.Lookup.TryGetValue(profileSetId, out var profileSetNode)
        || !profileSetNode.Entity.IsEntityType("IFCMATERIALPROFILESET")
        || profileSetNode.Entity.Count < 3
        // IfcMaterialProfileSet(Name, Description, MaterialProfiles, CompositeProfile)
        || profileSetNode.Entity[2] is not StepList profiles
        || profiles.Values.Count == 0
        || IfcGeometryReaders.AsId(profiles.Values[0]) is not { } profileId
        || !graph.Lookup.TryGetValue(profileId, out var profileNode)
        || !profileNode.Entity.IsEntityType("IFCMATERIALPROFILE")
        || profileNode.Entity.Count < 3
        // IfcMaterialProfile(Name, Description, Material, Profile, Priority, Category)
        || IfcGeometryReaders.AsId(profileNode.Entity[2]) is not { } innerMaterialId
      )
      {
        return false;
      }

      actualMaterialId = innerMaterialId;
    }
    else
    {
      // e.g. an IfcMaterialList or other unrecognized material entity - never guessed.
      return false;
    }

    if (
      !graph.Lookup.TryGetValue(actualMaterialId, out var actualMaterialNode)
      || !actualMaterialNode.Entity.IsEntityType("IFCMATERIAL")
      || actualMaterialNode.Entity.Count == 0
      || actualMaterialNode.Entity[0] is not StepString nameString
    )
    {
      return false;
    }

    materialName = nameString.Value.ToString();
    return !string.IsNullOrEmpty(materialName);
  }
}
