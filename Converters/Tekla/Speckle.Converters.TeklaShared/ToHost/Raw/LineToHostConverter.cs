using Speckle.Converters.Common;
using Speckle.Sdk.Models;

namespace Speckle.Converters.TeklaShared.ToHost.Raw;

public class LineToHostConverter : ITypedConverter<SOG.Line, TG.LineSegment>
{
  private readonly ITypedConverter<SOG.Point, TG.Point> _pointConverter;

  public LineToHostConverter(ITypedConverter<SOG.Point, TG.Point> pointConverter)
  {
    _pointConverter = pointConverter;
  }

  public TG.LineSegment Convert(SOG.Line target)
  {
    return new TG.LineSegment(_pointConverter.Convert(target.start), _pointConverter.Convert(target.end));
  }

  public object Convert(object target) => Convert((SOG.Line)target);
}
