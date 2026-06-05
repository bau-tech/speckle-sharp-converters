using Speckle.Converters.Common;
using Speckle.Sdk.Models;

namespace Speckle.Converters.TeklaShared.ToHost.Raw;

public class PointToHostConverter : ITypedConverter<SOG.Point, TG.Point>
{
  public TG.Point Convert(SOG.Point target)
  {
    return new TG.Point(target.x, target.y, target.z);
  }

  public object Convert(object target) => Convert((SOG.Point)target);
}
