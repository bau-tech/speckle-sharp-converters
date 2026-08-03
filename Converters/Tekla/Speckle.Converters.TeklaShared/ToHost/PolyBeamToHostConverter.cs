using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Objects.Data;
using Speckle.Sdk.Common.Exceptions;

namespace Speckle.Converters.TeklaShared.ToHost;

public class PolyBeamToHostConverter : ITypedConverter<TeklaObject, TSM.PolyBeam>
{
  private readonly ITypedConverter<SOG.Point, TG.Point> _pointConverter;

  public PolyBeamToHostConverter(ITypedConverter<SOG.Point, TG.Point> pointConverter)
  {
    _pointConverter = pointConverter;
  }

  public TSM.PolyBeam Convert(TeklaObject target)
  {
    var polyBeam = new TSM.PolyBeam();

    // A curved PolyBeam round-trips as an SOG.Arc location (see LocationExtractor.TryGetArcLocation
    // - the reverse of this), not a polyline: it's the exact 3-point CHAMFER_ARC_POINT contour, same
    // construction as RevitColumnBeamToTeklaBeamConverter.CreateArcPolyBeam.
    switch (target["location"])
    {
      case SOG.Arc arc:
        ApplyArcContourPoints(polyBeam, arc);
        break;
      case SOG.Polyline polyline:
        ApplyContourPoints(polyBeam, polyline);
        break;
      default:
        throw new ConversionException("PolyBeam requires a polyline or arc location.");
    }

    // Apply all shared Part properties (profile, material, position, numbering,
    // phase, deformation, UDAs) via the shared applicator.
    TeklaPartPropertyApplicator.Apply(polyBeam, target);

    polyBeam.Insert();
    return polyBeam;
  }

  private void ApplyArcContourPoints(TSM.PolyBeam polyBeam, SOG.Arc arc)
  {
    polyBeam.AddContourPoint(new TSM.ContourPoint(_pointConverter.Convert(arc.startPoint), new TSM.Chamfer()));
    polyBeam.AddContourPoint(
      new TSM.ContourPoint(
        _pointConverter.Convert(arc.midPoint),
        new TSM.Chamfer(0, 0, TSM.Chamfer.ChamferTypeEnum.CHAMFER_ARC_POINT)
      )
    );
    polyBeam.AddContourPoint(new TSM.ContourPoint(_pointConverter.Convert(arc.endPoint), new TSM.Chamfer()));
  }

  private static void ApplyContourPoints(TSM.PolyBeam polyBeam, SOG.Polyline polyline)
  {
    var chamfers = (polyline["chamfers"] as System.Collections.IEnumerable)?.Cast<object>().ToList();

    for (int i = 0; i * 3 + 2 < polyline.value.Count; i++)
    {
      var idx = i * 3;
      var pt = new TG.Point(polyline.value[idx], polyline.value[idx + 1], polyline.value[idx + 2]);
      var cp = new TSM.ContourPoint(pt, new TSM.Chamfer());

      if (chamfers != null && i < chamfers.Count && chamfers[i] is IDictionary<string, object> chMap)
      {
        cp.Chamfer.X = System.Convert.ToDouble((chMap.TryGetValue("x", out var cx) ? cx : 0.0) ?? 0.0);
        cp.Chamfer.Y = System.Convert.ToDouble((chMap.TryGetValue("y", out var cy) ? cy : 0.0) ?? 0.0);
        if (
          chMap.TryGetValue("type", out var typeStr)
          && typeStr is not null
          && Enum.TryParse<TSM.Chamfer.ChamferTypeEnum>(typeStr.ToString(), out var chamferType)
        )
          cp.Chamfer.Type = chamferType;
      }

      polyBeam.AddContourPoint(cp);
    }
  }

  public object Convert(object target) => Convert((TeklaObject)target);
}
