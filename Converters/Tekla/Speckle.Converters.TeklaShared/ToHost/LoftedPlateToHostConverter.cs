using System.Collections.Generic;
using System.Linq;
using Speckle.Converters.Common.Objects;
using Speckle.Objects.Data;
using Speckle.Sdk.Common.Exceptions;

namespace Speckle.Converters.TeklaShared.ToHost;

public class LoftedPlateToHostConverter : ITypedConverter<TeklaObject, TSM.LoftedPlate>
{
  public TSM.LoftedPlate Convert(TeklaObject target)
  {
    var props = target.properties;
    if (props is null || !props.ContainsKey("base_curves"))
      throw new ConversionException("LoftedPlate requires base_curves property.");

    var loftedPlate = new TSM.LoftedPlate();

    if (
      props.TryGetValue("face_type", out var ftObj)
      && ftObj is not null
      && System.Enum.TryParse<TSM.LoftedPlate.LoftedPlateFaceTypeEnum>(ftObj.ToString(), out var ftEnum)
    )
      loftedPlate.FaceType = ftEnum;

    // Each base curve is stored as a flat list of doubles (x,y,z pairs).
    // A 2-point curve (6 doubles) maps to TG.LineSegment, which implements ICurve.
    if (props.TryGetValue("base_curves", out var bcObj) && bcObj is IEnumerable<object> curvesEnum)
    {
      foreach (var curveRaw in curvesEnum)
      {
        if (curveRaw is not IEnumerable<object> ptsList)
          continue;
        var coords = ptsList.Select(p => System.Convert.ToDouble(p)).ToList();
        if (coords.Count >= 6)
        {
          loftedPlate.BaseCurves.Add(
            new TG.LineSegment(
              new TG.Point(coords[0], coords[1], coords[2]),
              new TG.Point(coords[3], coords[4], coords[5])
            )
          );
        }
      }
    }

    TeklaPartPropertyApplicator.Apply(loftedPlate, target);
    loftedPlate.Insert();
    return loftedPlate;
  }

  public object Convert(object target) => Convert((TeklaObject)target);
}
