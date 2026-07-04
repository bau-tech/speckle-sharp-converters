using Microsoft.Extensions.Logging;
using Speckle.Connectors.Common.Operations;
using Speckle.DoubleNumerics;
using Speckle.Objects.Data;
using Speckle.Sdk.Common;
using Speckle.Sdk.Models;
using Speckle.Sdk.Models.Collections;
using Speckle.Sdk.Models.Instances;
using SOGMesh = Speckle.Objects.Geometry.Mesh;

namespace Speckle.Connectors.TeklaShared.Operations.Receive;

/// <summary>
/// Replaces <see cref="InstanceProxy"/> entries in DataObject displayValues with world-space
/// copies of the referenced instance-definition meshes. Revit sends family-instance geometry in
/// symbol (local) space behind an InstanceProxy (definition meshes live in a root-level
/// "definitionGeometry" collection, the placement transform on the proxy) - the Tekla receive has
/// no instance baking, so without this step any geometry-based conversion logic (column Z extents,
/// bounding-box profiles, foundation sizes) sees no meshes at all and falls back to placement
/// points whose Z is not the element's real elevation.
/// </summary>
public static class InstanceGeometryMaterializer
{
  public static void Materialize(Base rootObject, IReadOnlyCollection<Base> atomicObjects, ILogger logger)
  {
    List<InstanceDefinitionProxy> definitionProxies = GetDefinitionProxies(rootObject);
    if (definitionProxies.Count == 0)
    {
      return;
    }

    Dictionary<string, SOGMesh> meshesByApplicationId = CollectMeshesByApplicationId(rootObject);

    var meshesByDefinitionId = new Dictionary<string, List<SOGMesh>>();
    foreach (InstanceDefinitionProxy proxy in definitionProxies)
    {
      if (proxy.applicationId is null)
      {
        continue;
      }
      var meshes = new List<SOGMesh>(proxy.objects.Count);
      foreach (string objectId in proxy.objects)
      {
        if (meshesByApplicationId.TryGetValue(objectId, out var mesh))
        {
          meshes.Add(mesh);
        }
      }
      meshesByDefinitionId[proxy.applicationId] = meshes;
    }

    int materializedCount = 0;
    int unresolvedCount = 0;
    foreach (Base atomicObject in atomicObjects)
    {
      if (atomicObject is not DataObject dataObject || !dataObject.displayValue.Any(d => d is InstanceProxy))
      {
        continue;
      }

      var newDisplayValue = new List<Base>(dataObject.displayValue.Count);
      foreach (Base item in dataObject.displayValue)
      {
        if (item is not InstanceProxy instanceProxy)
        {
          newDisplayValue.Add(item);
          continue;
        }

        if (
          !meshesByDefinitionId.TryGetValue(instanceProxy.definitionId, out var definitionMeshes)
          || definitionMeshes.Count == 0
        )
        {
          unresolvedCount++;
          continue;
        }

        foreach (SOGMesh definitionMesh in definitionMeshes)
        {
          newDisplayValue.Add(TransformMesh(definitionMesh, instanceProxy.transform, instanceProxy.units));
        }
        materializedCount++;
      }
      dataObject.displayValue = newDisplayValue;
    }

    logger.LogInformation(
      "Instance geometry: materialized {Materialized} instance proxies into world-space meshes"
        + " ({Definitions} definitions, {Meshes} definition meshes); {Unresolved} proxies had no resolvable definition",
      materializedCount,
      definitionProxies.Count,
      meshesByApplicationId.Count,
      unresolvedCount
    );
  }

  private static List<InstanceDefinitionProxy> GetDefinitionProxies(Base rootObject) =>
    rootObject[ProxyKeys.INSTANCE_DEFINITION] is IEnumerable<object> list
      ? list.OfType<InstanceDefinitionProxy>().ToList()
      : new List<InstanceDefinitionProxy>();

  // Definition meshes live in a root-level collection (Revit send: "definitionGeometry") - walk
  // the Collection tree only; meshes nested inside DataObjects are per-element geometry, not
  // shared definitions.
  private static Dictionary<string, SOGMesh> CollectMeshesByApplicationId(Base rootObject)
  {
    var result = new Dictionary<string, SOGMesh>();
    void Walk(Base current)
    {
      switch (current)
      {
        case SOGMesh mesh:
          if (mesh.applicationId is string id && !result.ContainsKey(id))
          {
            result[id] = mesh;
          }
          break;
        case Collection collection:
          foreach (Base child in collection.elements)
          {
            Walk(child);
          }
          break;
      }
    }
    Walk(rootObject);
    return result;
  }

  /// <summary>
  /// Applies an instance transform to a definition mesh, returning a new world-space mesh.
  /// Speckle matrix convention: row-major with the translation in M14/M24/M34
  /// (p' = M * [x y z 1]^T). Rotation/scale terms are unitless; the translation is expressed in
  /// the transform's own units and converted into the mesh's units before applying.
  /// </summary>
  private static SOGMesh TransformMesh(SOGMesh mesh, Matrix4x4 transform, string transformUnits)
  {
    double translationFactor = Units.GetConversionFactor(transformUnits, mesh.units);
    double t14 = transform.M14 * translationFactor;
    double t24 = transform.M24 * translationFactor;
    double t34 = transform.M34 * translationFactor;

    List<double> source = mesh.vertices;
    var vertices = new List<double>(source.Count);
    for (int i = 0; i + 2 < source.Count; i += 3)
    {
      double x = source[i];
      double y = source[i + 1];
      double z = source[i + 2];
      vertices.Add(transform.M11 * x + transform.M12 * y + transform.M13 * z + t14);
      vertices.Add(transform.M21 * x + transform.M22 * y + transform.M23 * z + t24);
      vertices.Add(transform.M31 * x + transform.M32 * y + transform.M33 * z + t34);
    }

    return new SOGMesh
    {
      vertices = vertices,
      faces = mesh.faces,
      colors = mesh.colors,
      units = mesh.units,
    };
  }
}
