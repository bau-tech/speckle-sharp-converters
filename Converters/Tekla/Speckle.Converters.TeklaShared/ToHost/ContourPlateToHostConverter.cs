using Speckle.Converters.Common.Objects;
using Speckle.Objects.Data;
using Speckle.Sdk.Common.Exceptions;

namespace Speckle.Converters.TeklaShared.ToHost;

public class ContourPlateToHostConverter : ITypedConverter<TeklaObject, TSM.ContourPlate>
{
  public TSM.ContourPlate Convert(TeklaObject target)
  {
    if (target["location"] is not SOG.Polyline polyline)
      throw new ConversionException("ContourPlate requires a polyline location.");

    var plate = new TSM.ContourPlate();
    ApplyContourPoints(plate, polyline);

    // Apply all shared Part properties (profile, material, position, numbering, phase, UDAs)
    TeklaPartPropertyApplicator.Apply(plate, target);

    plate.Insert();
    return plate;
  }

  private static void ApplyContourPoints(TSM.ContourPlate plate, SOG.Polyline polyline)
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
        if (
          chMap.TryGetValue("type", out var typeStr)
          && typeStr is not null
          && Enum.TryParse<TSM.Chamfer.ChamferTypeEnum>(typeStr.ToString(), out var chamferType)
        )
          cp.Chamfer.Type = chamferType;
      }

      plate.AddContourPoint(cp);
    }
  }

  public object Convert(object target) => Convert((TeklaObject)target);
}
