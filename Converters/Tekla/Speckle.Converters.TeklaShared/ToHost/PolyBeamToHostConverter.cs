using Speckle.Converters.Common.Objects;
using Speckle.Objects.Data;
using Speckle.Sdk.Common.Exceptions;

namespace Speckle.Converters.TeklaShared.ToHost;

public class PolyBeamToHostConverter : ITypedConverter<TeklaObject, TSM.PolyBeam>
{
  public TSM.PolyBeam Convert(TeklaObject target)
  {
    if (target["location"] is not SOG.Polyline polyline)
      throw new ConversionException("PolyBeam requires a polyline location.");

    var polyBeam = new TSM.PolyBeam();
    ApplyContourPoints(polyBeam, polyline);

    // Apply all shared Part properties (profile, material, position, numbering,
    // phase, deformation, UDAs) via the shared applicator.
    TeklaPartPropertyApplicator.Apply(polyBeam, target);

    polyBeam.Insert();
    return polyBeam;
  }

  private static void ApplyContourPoints(TSM.PolyBeam polyBeam, SOG.Polyline polyline)
  {
    var chamfers = (polyline["chamfers"] as System.Collections.IEnumerable)?.Cast<object>().ToList();

    for (int i = 0; i * 3 + 2 < polyline.value.Count; i++)
    {
      var idx = i * 3;
      var pt = new TG.Point(polyline.value[idx], polyline.value[idx + 1], polyline.value[idx + 2]);
      var cp = new TSM.ContourPoint(pt, new TSM.Chamfer());

      if (chamfers != null && i < chamfers.Count && chamfers[i] is IDictionary<string, object> chMap)
      {
        cp.Chamfer.X = System.Convert.ToDouble((chMap.TryGetValue("x", out var cx) ? cx : 0.0) ?? 0.0);
        cp.Chamfer.Y = System.Convert.ToDouble((chMap.TryGetValue("y", out var cy) ? cy : 0.0) ?? 0.0);
        if (chMap.TryGetValue("type", out var typeStr) && typeStr is not null)
          cp.Chamfer.Type = (TSM.Chamfer.ChamferTypeEnum)
            Enum.Parse(typeof(TSM.Chamfer.ChamferTypeEnum), typeStr.ToString());
      }

      polyBeam.AddContourPoint(cp);
    }
  }

  public object Convert(object target) => Convert((TeklaObject)target);
}
