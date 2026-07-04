using Speckle.Converters.Common.Objects;

namespace Speckle.Converters.RevitShared.ToHost.Raw.Geometry;

public class CurveArrayToCurveLoopConverter : ITypedConverter<DB.CurveArray, DB.CurveLoop>
{
  public DB.CurveLoop Convert(DB.CurveArray target)
  {
    DB.CurveLoop loop = new();
    foreach (DB.Curve curve in target)
    {
      loop.Append(curve);
    }

    return loop;
  }

  public object Convert(object target) => Convert((DB.CurveArray)target);
}
