using Speckle.Converters.Common;
using Speckle.Converters.TeklaShared.Helpers;
using Speckle.Sdk.Common;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.TeklaShared.ToHost;

/// <summary>
/// Last-resort fallback for any <see cref="Base"/> with no dedicated Tekla converter. Previously
/// fabricated a fixed 100mm HEA200 "Generic Placeholder" beam at the origin - a meaningless stub with
/// no relation to the source geometry's actual position, size, or shape. Now builds a real, correctly
/// shaped native part from the object's own display mesh(es) via Tekla's Shape Catalog + Brep
/// mechanism (<see cref="TSC.ShapeItem"/>/<see cref="TSM.Brep"/>): a shape's faceted-brep geometry is
/// authored once in the catalog (keyed by a name derived from the source object's id, updated in place
/// on re-receive) and instantiated as a <see cref="TSM.Brep"/> part - Tekla's Open API has no direct
/// "insert a freestanding part from arbitrary mesh/BREP data" call; this two-step catalog+part path is
/// the only way to get one. Untested against a live model as of writing: whether a Brep's authored
/// FacetedBrep coordinates map 1:1 onto world space when StartPoint=(0,0,0)/EndPoint=(1,0,0) (chosen to
/// keep the shape's local axes aligned with world axes, since we bake already-world-space mesh
/// coordinates directly into the shape rather than a true local/authored frame) is a best-effort
/// assumption pending live verification.
/// </summary>
public class GeometricItemToHostConverter : ITypedConverter<Base, TSM.ModelObject>
{
  private const string DEFAULT_MATERIAL = "S235JR";

  public TSM.ModelObject Convert(Base target)
  {
    var meshes = target is SOG.Mesh m ? new List<SOG.Mesh> { m } : GetMeshes(target);
    if (meshes.Count == 0)
    {
      throw new ConversionException(
        $"No native Tekla conversion and no displayable geometry available for '{target.speckle_type}'."
      );
    }

    var (vertices, outerWires) = BuildBrepGeometry(meshes);
    if (vertices.Length < 3 || outerWires.Length == 0)
    {
      throw new ConversionException($"'{target.speckle_type}' mesh geometry has no usable faces for a Tekla shape.");
    }

    var facetedBrep = new TG.FacetedBrep(vertices, outerWires, new Dictionary<int, int[][]>());
    string shapeName = SanitizeShapeName($"Speckle_{target.applicationId ?? target.id}");

    var shapeItem = new TSC.ShapeItem { Name = shapeName, ShapeFacetedBrep = facetedBrep, UpAxis = TSC.ShapeUpAxis.Z_Axis };

    if (shapeItem.Select())
    {
      // Keep the catalog shape in sync with the source geometry on every re-receive, rather than
      // silently reusing whatever was registered under this name on a previous receive.
      shapeItem.ShapeFacetedBrep = facetedBrep;
      if (!shapeItem.Modify())
      {
        throw new ConversionException($"Failed to update Tekla shape catalog entry for '{target.speckle_type}'.");
      }
    }
    else if (!shapeItem.Insert())
    {
      throw new ConversionException($"Failed to insert Tekla shape catalog entry for '{target.speckle_type}'.");
    }

    TSM.Brep brep = new(new TG.Point(0, 0, 0), new TG.Point(1, 0, 0))
    {
      Profile = { ProfileString = shapeItem.Name },
      Material = { MaterialString = DEFAULT_MATERIAL },
      Name = target.speckle_type,
    };

    if (!brep.Insert())
    {
      throw new ConversionException($"Failed to insert Tekla Brep part for '{target.speckle_type}'.");
    }

    return brep;
  }

  private static (TG.Vector[] Vertices, int[][] OuterWires) BuildBrepGeometry(List<SOG.Mesh> meshes)
  {
    var vertices = new List<TG.Vector>();
    var outerWires = new List<int[]>();

    foreach (var mesh in meshes)
    {
      double scale = RevitPropertyReader.GetUnitScaleFactor(mesh.units, Units.Millimeters);
      int vertexOffset = vertices.Count;

      for (int i = 0; i + 2 < mesh.vertices.Count; i += 3)
      {
        vertices.Add(new TG.Vector(mesh.vertices[i] * scale, mesh.vertices[i + 1] * scale, mesh.vertices[i + 2] * scale));
      }

      int f = 0;
      while (f < mesh.faces.Count)
      {
        int n = mesh.faces[f];
        if (n < 3 || f + n >= mesh.faces.Count)
        {
          // Malformed face list - stop rather than throw away an otherwise-usable partial mesh.
          break;
        }
        var face = new int[n];
        for (int k = 0; k < n; k++)
        {
          face[k] = mesh.faces[f + 1 + k] + vertexOffset;
        }
        outerWires.Add(face);
        f += n + 1;
      }
    }

    return (vertices.ToArray(), outerWires.ToArray());
  }

  private static string SanitizeShapeName(string raw)
  {
    // ShapeItem.Name allows up to 1023 chars; keep it well short of that and free of characters that
    // might be unsafe in the catalog's own storage (it persists shape geometry to disk by this name).
    var cleaned = new string(raw.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
    return cleaned.Length > 200 ? cleaned[..200] : cleaned;
  }

  private List<SOG.Mesh> GetMeshes(Base target)
  {
    var meshes = new List<SOG.Mesh>();
    if (target["displayValue"] is System.Collections.IEnumerable displayValues)
    {
      foreach (var dv in displayValues)
      {
        if (dv is SOG.Mesh mesh)
        {
          meshes.Add(mesh);
        }
      }
    }
    return meshes;
  }

  public object Convert(object target) => Convert((Base)target);
}
