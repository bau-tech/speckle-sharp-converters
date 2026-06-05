using Speckle.Converters.Common;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.TeklaShared.ToHost;

public class BuiltElementBeamToHostConverter : ITypedConverter<Base, TSM.Beam>
{
  private readonly ITypedConverter<SOG.Line, TG.LineSegment> _lineConverter;

  public BuiltElementBeamToHostConverter(ITypedConverter<SOG.Line, TG.LineSegment> lineConverter)
  {
    _lineConverter = lineConverter;
  }

  public TSM.Beam Convert(Base target)
  {
    if (target["baseLine"] is not SOG.Line line)
    {
      throw new ConversionException("Tekla Beam requires a line baseLine.");
    }

    var lineSegment = _lineConverter.Convert(line);

    TSM.Beam beam = new TSM.Beam(lineSegment.Point1, lineSegment.Point2);

    beam.Profile.ProfileString = target["profile"] as string ?? "HEA200";
    beam.Material.MaterialString = target["material"] as string ?? "S235JR";

    beam.Insert();
    return beam;
  }

  public object Convert(object target) => Convert((Base)target);
}
