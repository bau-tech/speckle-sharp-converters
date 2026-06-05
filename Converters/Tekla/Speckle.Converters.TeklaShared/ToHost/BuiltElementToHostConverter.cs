using Speckle.Converters.Common;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.TeklaShared.ToHost;

public class BuiltElementToHostConverter : ITypedConverter<Base, TSM.ModelObject>
{
  private readonly BuiltElementBeamToHostConverter _beamConverter;
  private readonly BuiltElementColumnToHostConverter _columnConverter;

  public BuiltElementToHostConverter(
    BuiltElementBeamToHostConverter beamConverter,
    BuiltElementColumnToHostConverter columnConverter
  )
  {
    _beamConverter = beamConverter;
    _columnConverter = columnConverter;
  }

  public TSM.ModelObject Convert(Base target)
  {
    string speckleType = target.speckle_type;

    if (speckleType.Contains("Beam"))
    {
      return _beamConverter.Convert(target);
    }
    if (speckleType.Contains("Column"))
    {
      return _columnConverter.Convert(target);
    }

    throw new ConversionException($"Speckle type {speckleType} not supported for native Tekla conversion.");
  }

  public object Convert(object target) => Convert((Base)target);
}
