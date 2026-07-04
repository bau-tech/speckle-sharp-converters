using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Objects.Data;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.TeklaShared.ToHost;

public class BeamToHostConverter : ITypedConverter<TeklaObject, TSM.Beam>
{
  private readonly ITypedConverter<SOG.Line, TG.LineSegment> _lineConverter;

  public BeamToHostConverter(ITypedConverter<SOG.Line, TG.LineSegment> lineConverter)
  {
    _lineConverter = lineConverter;
  }

  public TSM.Beam Convert(TeklaObject target)
  {
    if (target["location"] is not SOG.Line line)
      throw new ConversionException("Tekla Beam requires a line location.");

    var lineSegment = _lineConverter.Convert(line);
    var beam = new TSM.Beam(lineSegment.Point1, lineSegment.Point2);

    // Apply all shared Part properties (profile, material, position, numbering,
    // phase, deformation, end offsets, UDAs) via the shared applicator.
    TeklaPartPropertyApplicator.Apply(beam, target);

    beam.Insert();
    return beam;
  }

  public object Convert(object target) => Convert((TeklaObject)target);
}
