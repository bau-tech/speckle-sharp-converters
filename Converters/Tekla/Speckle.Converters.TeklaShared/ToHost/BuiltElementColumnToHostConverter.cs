using Speckle.Converters.Common;
using Speckle.Converters.TeklaShared.Helpers;
using Speckle.Converters.TeklaShared.Helpers.ProfileMapping;
using Speckle.Sdk.Common;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.TeklaShared.ToHost;

public class BuiltElementColumnToHostConverter : ITypedConverter<Base, TSM.Beam>
{
  private const string DEFAULT_PROFILE = "HEA200";
  private const string DEFAULT_MATERIAL = "S235JR";

  private readonly ITypedConverter<SOG.Line, TG.LineSegment> _lineConverter;
  private readonly TeklaCatalogValidator _catalogValidator;
  private readonly ConversionWarningCollector _warnings;

  public BuiltElementColumnToHostConverter(
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
      throw new ConversionException("Tekla Column requires a line baseLine.");
    }

    // Tekla model coordinates are always millimeters (see PointToHostConverter) - this generic/IFC
    // receive path fed the raw (un-scaled) baseLine straight into the point/line converter, so
    // anything captured in a non-mm unit landed at the wrong scale in the Tekla model.
    double scale = RevitPropertyReader.GetUnitScaleFactor(line.units, Units.Millimeters);
    var lineSegment = _lineConverter.Convert(RevitPropertyReader.ScaleLine(line, scale));

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
