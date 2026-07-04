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
      case TSM.SpiralBeam spiralBeam:
        return _lineConverter.Convert(new TG.LineSegment(spiralBeam.StartPoint, spiralBeam.EndPoint));

      case TSM.LoftedPlate loftedPlate:
        if (loftedPlate.BaseCurves.Count > 0 && loftedPlate.BaseCurves[0] is TG.LineSegment firstSeg)
          return new SOG.Polyline
          {
            value = new System.Collections.Generic.List<double>
            {
              firstSeg.Point1.X, firstSeg.Point1.Y, firstSeg.Point1.Z,
              firstSeg.Point2.X, firstSeg.Point2.Y, firstSeg.Point2.Z
            },
            units = "mm"
          };
        return null;

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

      case TSM.BentPlate:
        return null; // BentPlate uses ConnectiveGeometry in 2024+; location not extractable via ContourPoints

      case TSM.RebarMesh rebarMesh:
        if (rebarMesh.Polygon?.Points is not null && rebarMesh.Polygon.Points.Count > 0)
          return GetPolylineFromPoints(rebarMesh.Polygon.Points.Cast<TG.Point>().ToList());
        return null;

      case TSM.SingleRebar singleRebar:
        return GetPolylineFromPoints(singleRebar.Polygon.Points.Cast<TG.Point>().ToList());

      case TSM.RebarGroup rebarGroup:
        if (rebarGroup.Polygons.Count > 0 && rebarGroup.Polygons[0] is TSM.Polygon poly)
        {
          return GetPolylineFromPoints(poly.Points.Cast<TG.Point>().ToList());
        }
        return null;

      case TSM.RebarSet rebarSet:
        // RebarSets have no single defining curve — their shape is described by leg faces (each
        // a contour) and guidelines. Use the first leg face's contour as a representative outline,
        // matching the RebarGroup approach above, so the object has a renderable `location`.
        if (
          rebarSet.LegFaces.Count > 0
          && rebarSet.LegFaces[0].Contour?.ContourPoints is { Count: > 0 } contourPoints
        )
        {
          return GetPolylineFromPoints(contourPoints.Cast<TG.Point>().ToList());
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
