using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Sdk.Models;

namespace Speckle.Converters.TeklaShared.ToSpeckle.Helpers;

public class LocationExtractor
{
  private readonly ITypedConverter<TG.Point, SOG.Point> _pointConverter;
  private readonly ITypedConverter<TG.LineSegment, SOG.Line> _lineConverter;

  public LocationExtractor(
    ITypedConverter<TG.Point, SOG.Point> pointConverter,
    ITypedConverter<TG.LineSegment, SOG.Line> lineConverter
  )
  {
    _pointConverter = pointConverter;
    _lineConverter = lineConverter;
  }

  public Base? GetLocation(TSM.ModelObject modelObject)
  {
    switch (modelObject)
    {
      case TSM.Beam beam:
        return _lineConverter.Convert(new TG.LineSegment(beam.StartPoint, beam.EndPoint));

      case TSM.ContourPlate plate:
        return GetPolylineFromPoints(plate.Contour.ContourPoints.Cast<TSM.ContourPoint>().ToList());

      case TSM.PolyBeam polybeam:
        return GetPolylineFromPoints(polybeam.Contour.ContourPoints.Cast<TSM.ContourPoint>().ToList());

      case TSM.Fitting fitting:
        return _lineConverter.Convert(
          new TG.LineSegment(fitting.Plane.Origin, fitting.Plane.Origin + fitting.Plane.AxisX * 100)
        );

      case TSM.SingleRebar singleRebar:
        return GetPolylineFromPoints(singleRebar.Polygon.Points.Cast<TG.Point>().ToList());

      case TSM.RebarGroup rebarGroup:
        if (rebarGroup.Polygons.Count > 0 && rebarGroup.Polygons[0] is TSM.Polygon poly)
        {
          return GetPolylineFromPoints(poly.Points.Cast<TG.Point>().ToList());
        }
        return null;

      default:
        return null;
    }
  }

  private SOG.Polyline GetPolylineFromPoints(System.Collections.Generic.List<TG.Point> points)
  {
    return new SOG.Polyline
    {
      value = points.SelectMany(p => new List<double> { p.X, p.Y, p.Z }).ToList(),
      closed = false,
      units = "mm"
    };
  }

  private SOG.Polyline GetPolylineFromPoints(System.Collections.Generic.List<TSM.ContourPoint> points)
  {
    var polyline = new SOG.Polyline
    {
      value = points.SelectMany(p => new List<double> { p.X, p.Y, p.Z }).ToList(),
      closed = true,
      units = "mm"
    };

    // Store chamfers in a metadata dictionary on the polyline
    var chamfers = new List<Dictionary<string, object?>>();
    foreach (var pt in points)
    {
      chamfers.Add(
        new Dictionary<string, object?>
        {
          ["type"] = pt.Chamfer.Type.ToString(),
          ["x"] = pt.Chamfer.X,
          ["y"] = pt.Chamfer.Y
        }
      );
    }
    polyline["chamfers"] = chamfers;

    return polyline;
  }
}
