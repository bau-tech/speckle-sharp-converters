using System.Collections.Generic;
using System.Linq;
using Speckle.Converters.Common.Objects;
using Speckle.Objects.Data;
using Speckle.Sdk.Common.Exceptions;

namespace Speckle.Converters.TeklaShared.ToHost;

public class SpiralBeamToHostConverter : ITypedConverter<TeklaObject, TSM.SpiralBeam>
{
  public TSM.SpiralBeam Convert(TeklaObject target)
  {
    if (target["location"] is not SOG.Line line)
      throw new ConversionException("SpiralBeam requires a line location.");

    var spiralBeam = new TSM.SpiralBeam();
    spiralBeam.StartPoint = new TG.Point(line.start.x, line.start.y, line.start.z);

    var props = target.properties;
    if (props is not null)
    {
      if (props.TryGetValue("total_rise", out var tr) && tr != null)
        spiralBeam.TotalRise = System.Convert.ToDouble(tr);
      if (props.TryGetValue("rotation_angle", out var ra) && ra != null)
        spiralBeam.RotationAngle = System.Convert.ToDouble(ra);
      if (props.TryGetValue("twist_angle_start", out var tas) && tas != null)
        spiralBeam.TwistAngleStart = System.Convert.ToDouble(tas);
      if (props.TryGetValue("twist_angle_end", out var tae) && tae != null)
        spiralBeam.TwistAngleEnd = System.Convert.ToDouble(tae);
      if (props.TryGetValue("rotation_axis_base", out var rab))
        spiralBeam.RotationAxisBasePoint = MapPoint(rab);
      if (props.TryGetValue("rotation_axis_up", out var rau))
        spiralBeam.RotationAxisUpPoint = MapPoint(rau);
      // RotationCenterPoint, RotationAxisDirection, EndPoint are computed (read-only)
    }

    TeklaPartPropertyApplicator.Apply(spiralBeam, target);
    spiralBeam.Insert();
    return spiralBeam;
  }

  private static TG.Point MapPoint(object? obj)
  {
    if (obj is IEnumerable<object> ptsObj)
    {
      var list = ptsObj.Select(p => System.Convert.ToDouble(p)).ToList();
      if (list.Count >= 3)
        return new TG.Point(list[0], list[1], list[2]);
    }
    return new TG.Point(0, 0, 0);
  }

  public object Convert(object target) => Convert((TeklaObject)target);
}
