using Speckle.Converters.Common;
using Speckle.Converters.TeklaShared.Helpers;
using Speckle.Converters.TeklaShared.Helpers.ProfileMapping;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.TeklaShared.ToHost;

public class BuiltElementBeamToHostConverter : ITypedConverter<Base, TSM.Beam>
{
  private const string DEFAULT_PROFILE = "HEA200";
  private const string DEFAULT_MATERIAL = "S235JR";

  private readonly ITypedConverter<SOG.Line, TG.LineSegment> _lineConverter;
  private readonly TeklaCatalogValidator _catalogValidator;
  private readonly ConversionWarningCollector _warnings;

  public BuiltElementBeamToHostConverter(
    ITypedConverter<SOG.Line, TG.LineSegment> lineConverter,
    TeklaCatalogValidator catalogValidator,
    ConversionWarningCollector warnings
  )
  {
    _lineConverter = lineConverter;
    _catalogValidator = catalogValidator;
    _warnings = warnings;
  }

  public TSM.Beam Convert(Base target)
  {
    if (target["baseLine"] is not SOG.Line line)
    {
      throw new ConversionException("Tekla Beam requires a line baseLine.");
    }

    var lineSegment = _lineConverter.Convert(line);

    TSM.Beam beam = new TSM.Beam(lineSegment.Point1, lineSegment.Point2);

    var (profile, profileWarning) = _catalogValidator.ValidateOrFallback(
      target["profile"] as string,
      DEFAULT_PROFILE,
      isProfile: true
    );
    beam.Profile.ProfileString = profile;
    if (profileWarning != null)
    {
      _warnings.Add(target.id, profileWarning);
    }

    var (material, materialWarning) = _catalogValidator.ValidateOrFallback(
      target["material"] as string,
      DEFAULT_MATERIAL,
      isProfile: false
    );
    beam.Material.MaterialString = material;
    if (materialWarning != null)
    {
      _warnings.Add(target.id, materialWarning);
    }

    beam.Insert();
    return beam;
  }

  public object Convert(object target) => Convert((Base)target);
}
