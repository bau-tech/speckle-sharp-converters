using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Sdk.Models;

namespace Speckle.Converters.TeklaShared.ToSpeckle.Helpers;

public class LocationExtractor
{
  private readonly ITypedConverter<TG.Point, SOG.Point> _pointConverter;
  private readonly ITypedConverter<TG.LineSegment, SOG.Line> _lineConverter;
  private readonly ILogger<LocationExtractor> _logger;

  public LocationExtractor(
    ITypedConverter<TG.Point, SOG.Point> pointConverter,
    ITypedConverter<TG.LineSegment, SOG.Line> lineConverter,
    ILogger<LocationExtractor> logger
  )
  {
    _pointConverter = pointConverter;
    _lineConverter = lineConverter;
    _logger = logger;
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
              firstSeg.Point1.X,
              firstSeg.Point1.Y,
              firstSeg.Point1.Z,
              firstSeg.Point2.X,
              firstSeg.Point2.Y,
              firstSeg.Point2.Z,
            },
            units = "mm",
          };
        return null;

      case TSM.Beam beam:
        return _lineConverter.Convert(new TG.LineSegment(beam.StartPoint, beam.EndPoint));

      case TSM.ContourPlate plate:
        return GetPolylineFromPoints(plate.Contour.ContourPoints.Cast<TSM.ContourPoint>().ToList());

      case TSM.PolyBeam polybeam:
      {
        var polyBeamPoints = polybeam.Contour.ContourPoints.Cast<TSM.ContourPoint>().ToList();
        _logger.LogInformation(
          "LocationExtractor: PolyBeam contour has {Count} point(s), chamfer types=[{Types}]",
          polyBeamPoints.Count,
          string.Join(", ", polyBeamPoints.Select(p => p.Chamfer.Type.ToString()))
        );
        return TryGetArcLocation(polyBeamPoints) is { } arcLocation
          ? arcLocation
          : GetPolylineFromPoints(polyBeamPoints);
      }

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
        if (rebarSet.LegFaces.Count > 0 && rebarSet.LegFaces[0].Contour?.ContourPoints is { Count: > 0 } contourPoints)
        {
          return GetPolylineFromPoints(contourPoints.Cast<TG.Point>().ToList());
        }
        return null;

      default:
        return null;
    }
  }

  /// <summary>
  /// A curved PolyBeam (the only way a beam or wall panel is curved in Tekla - plain TSM.Beam has no
  /// arc concept) is authored as exactly 3 contour points with the middle one flagged
  /// CHAMFER_ARC_POINT (see RevitColumnBeamToTeklaBeamConverter.CreateArcPolyBeam, the mirror-image
  /// case for the reverse direction) - detect that shape and return a true SOG.Arc instead of a
  /// chord-vertex polyline. Any other PolyBeam shape (more points, no arc chamfer) is a genuine
  /// multi-vertex sketch, left to the caller's polyline fallback.
  /// </summary>
  private SOG.Arc? TryGetArcLocation(List<TSM.ContourPoint> points)
  {
    if (points.Count != 3 || points[1].Chamfer.Type != TSM.Chamfer.ChamferTypeEnum.CHAMFER_ARC_POINT)
    {
      return null;
    }

    SOG.Point start = _pointConverter.Convert(new TG.Point(points[0].X, points[0].Y, points[0].Z));
    SOG.Point mid = _pointConverter.Convert(new TG.Point(points[1].X, points[1].Y, points[1].Z));
    SOG.Point end = _pointConverter.Convert(new TG.Point(points[2].X, points[2].Y, points[2].Z));

    return new SOG.Arc
    {
      startPoint = start,
      midPoint = mid,
      endPoint = end,
      plane = BuildPlane(start, mid, end),
      units = "mm",
    };
  }

  // Only consulted by ArcConverterToHost's degenerate full-circle branch (start==end), never hit by
  // a real 3-point arc - exact orientation isn't load-bearing, just needs to be a valid plane
  // through the 3 points so nothing downstream sees a null/degenerate one.
  private static SOG.Plane BuildPlane(SOG.Point start, SOG.Point mid, SOG.Point end)
  {
    SOG.Vector v1 = new(mid.x - start.x, mid.y - start.y, mid.z - start.z, "mm");
    SOG.Vector v2 = new(end.x - start.x, end.y - start.y, end.z - start.z, "mm");
    SOG.Vector normal = Normalize(Cross(v1, v2));
    SOG.Vector xdir = Normalize(v2);
    SOG.Vector ydir = Normalize(Cross(normal, xdir));

    return new SOG.Plane
    {
      origin = mid,
      normal = normal,
      xdir = xdir,
      ydir = ydir,
      units = "mm",
    };
  }

  private static SOG.Vector Cross(SOG.Vector a, SOG.Vector b) =>
    new(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x, "mm");

  private static SOG.Vector Normalize(SOG.Vector v)
  {
    double length = Math.Sqrt((v.x * v.x) + (v.y * v.y) + (v.z * v.z));
    return length < 1e-9 ? v : new SOG.Vector(v.x / length, v.y / length, v.z / length, "mm");
  }

  private SOG.Polyline GetPolylineFromPoints(System.Collections.Generic.List<TG.Point> points)
  {
    return new SOG.Polyline
    {
      value = points.SelectMany(p => new List<double> { p.X, p.Y, p.Z }).ToList(),
      closed = false,
      units = "mm",
    };
  }

  private SOG.Polyline GetPolylineFromPoints(System.Collections.Generic.List<TSM.ContourPoint> points)
  {
    var polyline = new SOG.Polyline
    {
      value = points.SelectMany(p => new List<double> { p.X, p.Y, p.Z }).ToList(),
      closed = true,
      units = "mm",
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
          ["y"] = pt.Chamfer.Y,
        }
      );
    }
    polyline["chamfers"] = chamfers;

    return polyline;
  }
}
