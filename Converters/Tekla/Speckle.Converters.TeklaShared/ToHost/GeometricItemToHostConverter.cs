using Speckle.Converters.Common;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.TeklaShared.ToHost;

public class GeometricItemToHostConverter : ITypedConverter<Base, TSM.ModelObject>
{
  public TSM.ModelObject Convert(Base target)
  {
    // 1. Find display meshes
    var meshes = target is SOG.Mesh m ? new List<SOG.Mesh> { m } : GetMeshes(target);

    if (meshes.Count == 0)
    {
      throw new ConversionException("No displayable geometry found for generic conversion.");
    }

    // 2. Create Tekla Placeholder (Beam) for generic geometry
    // TODO: Implement actual Tekla 'Item' for high-fidelity Brep/Mesh import
    TSM.Beam placeholder = new TSM.Beam(new TG.Point(0, 0, 0), new TG.Point(100, 0, 0));
    placeholder.Profile.ProfileString = "HEA200";
    placeholder.Name = "Generic Placeholder";

    placeholder.Insert();
    return placeholder;
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
