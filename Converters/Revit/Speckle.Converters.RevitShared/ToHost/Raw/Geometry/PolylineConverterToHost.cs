using Autodesk.Revit.DB;
using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Converters.RevitShared.Services;
using Speckle.Converters.RevitShared.Settings;
using Speckle.Objects.Geometry;

namespace Speckle.Converters.RevitShared.ToSpeckle;

public class PolylineConverterToHost : ITypedConverter<SOG.Polyline, DB.CurveArray>
{
  private readonly ITypedConverter<SOG.Line, DB.Line> _lineConverter;
  private readonly ITypedConverter<SOG.Arc, DB.Arc> _arcConverter;
  private readonly ScalingServiceToHost _scalingService;
  private readonly IConverterSettingsStore<RevitConversionSettings> _converterSettings;

  public PolylineConverterToHost(
    ITypedConverter<SOG.Line, DB.Line> lineConverter,
    ITypedConverter<SOG.Arc, DB.Arc> arcConverter,
    ScalingServiceToHost scalingService,
    IConverterSettingsStore<RevitConversionSettings> converterSettings
  )
  {
    _lineConverter = lineConverter;
    _arcConverter = arcConverter;
    _scalingService = scalingService;
    _converterSettings = converterSettings;
  }

  public CurveArray Convert(Polyline target)
  {
    var curveArray = new CurveArray();
    if (target.value.Count == 6)
    {
      // 6 coordinate values (two sets of 3), so polyline is actually a single line
      curveArray.Append(_lineConverter.Convert(new SOG.Line(target.value, target.units)));
      return curveArray;
    }

    var pts = target.GetPoints();
    var fillets = BuildFillets(target, pts);

    // Each vertex j contributes a straight edge INTO it (from the previous vertex's exit point to
    // its own entry point) and, if rounded, an arc replacing the corner (entry -> exit via mid).
    // Unrounded vertices have entry == exit == the raw point, so this reduces to the original
    // vertex-to-vertex line chain when no chamfers are present - fully backward compatible.
    int edgeCount = target.closed ? pts.Count : pts.Count - 1;
    for (int i = 0; i < edgeCount; i++)
    {
      int j = (i + 1) % pts.Count;
      SOG.Point lineStart = fillets[i]?.Exit ?? pts[i];
      SOG.Point lineEnd = fillets[j]?.Entry ?? pts[j];

      TryAppendLineSafely(
        curveArray,
        new SOG.Line
        {
          start = lineStart,
          end = lineEnd,
          units = target.units,
        }
      );

      if (fillets[j] is { } fillet)
      {
        TryAppendArcSafely(curveArray, fillet, target.units);
      }
    }

    return curveArray;
  }

  /// <summary>
  /// A tangent-arc replacement for one sharp corner: <see cref="Entry"/>/<see cref="Exit"/> are the
  /// trimmed-back points along the incoming/outgoing edges where the fillet begins/ends,
  /// <see cref="Mid"/>/<see cref="Center"/> define the arc itself.
  /// </summary>
  private readonly record struct CornerFillet(SOG.Point Entry, SOG.Point Exit, SOG.Point Mid, SOG.Point Center);

  /// <summary>
  /// Reconstructs Tekla's per-corner rounding (<c>CHAMFER_ROUNDING</c>, captured on the send side as a
  /// "chamfers" dynamic property parallel to the polyline's points - see LocationExtractor on the
  /// Tekla converter project) as a fillet arc. Falls back to a sharp corner (null) for any vertex with
  /// no rounding, a non-positive/missing radius, or degenerate edge geometry (near-zero-length edges,
  /// a near-straight or near-doubled-back corner, or a radius too large to fit the adjacent edges) -
  /// producing a sharp corner there is far better than emitting invalid/self-intersecting geometry.
  /// </summary>
  private CornerFillet?[] BuildFillets(Polyline target, System.Collections.Generic.IReadOnlyList<SOG.Point> pts)
  {
    int n = pts.Count;
    var result = new CornerFillet?[n];

    if (target["chamfers"] is not System.Collections.IEnumerable chamfersEnum)
    {
      return result;
    }

    var chamfers = new System.Collections.Generic.List<object>();
    foreach (var c in chamfersEnum)
    {
      chamfers.Add(c);
    }

    for (int i = 0; i < n; i++)
    {
      if (!target.closed && (i == 0 || i == n - 1))
      {
        // True open-polyline endpoints are not corners - nothing to fillet.
        continue;
      }

      if (i >= chamfers.Count || chamfers[i] is not System.Collections.Generic.IDictionary<string, object> chamfer)
      {
        continue;
      }

      if (
        !chamfer.TryGetValue("type", out var typeObj)
        || typeObj?.ToString() != "CHAMFER_ROUNDING"
        || !chamfer.TryGetValue("x", out var radiusObj)
        || radiusObj is null
      )
      {
        continue;
      }

      double radius = System.Convert.ToDouble(radiusObj);
      if (radius <= 0)
      {
        continue;
      }

      int prevIdx = target.closed ? (i - 1 + n) % n : i - 1;
      int nextIdx = target.closed ? (i + 1) % n : i + 1;

      if (TryComputeFillet(pts[prevIdx], pts[i], pts[nextIdx], radius) is { } fillet)
      {
        result[i] = fillet;
      }
    }

    return result;
  }

  private static CornerFillet? TryComputeFillet(SOG.Point prev, SOG.Point curr, SOG.Point next, double radius)
  {
    const double ANGLE_EPSILON = 1e-6;

    (double X, double Y, double Z) toPrev = (prev.x - curr.x, prev.y - curr.y, prev.z - curr.z);
    (double X, double Y, double Z) toNext = (next.x - curr.x, next.y - curr.y, next.z - curr.z);
    double len1 = Length(toPrev);
    double len2 = Length(toNext);
    if (len1 < 1e-9 || len2 < 1e-9)
    {
      return null;
    }

    var v1 = Scale(toPrev, 1 / len1);
    var v2 = Scale(toNext, 1 / len2);

    double dot = Math.Clamp(Dot(v1, v2), -1, 1);
    double angle = Math.Acos(dot);
    if (angle < ANGLE_EPSILON || angle > Math.PI - ANGLE_EPSILON)
    {
      // Edges double back on themselves (spike) or are already straight (no real corner) - a fillet
      // isn't meaningful either way.
      return null;
    }

    double tangentLength = radius / Math.Tan(angle / 2);
    if (tangentLength <= 0 || double.IsNaN(tangentLength) || tangentLength >= len1 || tangentLength >= len2)
    {
      // Radius too large for the adjacent edges - would eat into the neighbouring corner.
      return null;
    }

    var bisector = Normalize(Add(v1, v2));
    if (bisector is (0, 0, 0))
    {
      return null;
    }

    double centerDistance = radius / Math.Sin(angle / 2);
    (double X, double Y, double Z) currTuple = (curr.x, curr.y, curr.z);
    var center = Add(currTuple, Scale(bisector, centerDistance));
    var toCorner = Normalize(Sub(currTuple, center));
    var mid = Add(center, Scale(toCorner, radius));

    var entry = Add(currTuple, Scale(v1, tangentLength));
    var exit = Add(currTuple, Scale(v2, tangentLength));

    return new CornerFillet(
      ToPoint(entry, curr.units),
      ToPoint(exit, curr.units),
      ToPoint(mid, curr.units),
      ToPoint(center, curr.units)
    );
  }

  private void TryAppendArcSafely(CurveArray curveArray, CornerFillet fillet, string units)
  {
    var entryFromCenter = Sub(
      (fillet.Entry.x, fillet.Entry.y, fillet.Entry.z),
      (fillet.Center.x, fillet.Center.y, fillet.Center.z)
    );
    var exitFromCenter = Sub(
      (fillet.Exit.x, fillet.Exit.y, fillet.Exit.z),
      (fillet.Center.x, fillet.Center.y, fillet.Center.z)
    );
    var normal = Normalize(Cross(entryFromCenter, exitFromCenter));
    var xdir = Normalize(entryFromCenter);
    var ydir = Cross(normal, xdir);

    var arc = new SOG.Arc
    {
      startPoint = fillet.Entry,
      midPoint = fillet.Mid,
      endPoint = fillet.Exit,
      units = units,
      plane = new SOG.Plane
      {
        origin = fillet.Center,
        normal = ToVector(normal, units),
        xdir = ToVector(xdir, units),
        ydir = ToVector(ydir, units),
        units = units,
      },
    };

    if (
      _scalingService.ScaleToNative(arc.length, units)
      < _converterSettings.Current.Document.Application.ShortCurveTolerance
    )
    {
      return;
    }

    curveArray.Append(_arcConverter.Convert(arc));
  }

  /// <summary>
  /// Checks if a Speckle <see cref="SOG.Line"/> is too sort to be created in Revit.
  /// </summary>
  /// <remarks>
  /// The length of the line will be computed on the spot to ensure it is accurate.
  /// </remarks>
  /// <param name="line">The <see cref="SOG.Line"/> to be tested.</param>
  /// <returns>true if the line is too short, false otherwise.</returns>
  public bool IsLineTooShort(SOG.Line line)
  {
    var scaleToNative = _scalingService.ScaleToNative(SOG.Point.Distance(line.start, line.end), line.units);
    return scaleToNative < _converterSettings.Current.Document.Application.ShortCurveTolerance;
  }

  /// <summary>
  /// Attempts to append a Speckle <see cref="SOG.Line"/> onto a Revit <see cref="CurveArray"/>.
  /// This method ensures the line is long enough to be supported.
  /// It will also convert the line to Revit before appending it to the <see cref="CurveArray"/>.
  /// </summary>
  /// <param name="curveArray">The revit <see cref="CurveArray"/> to add the line to.</param>
  /// <param name="line">The <see cref="SOG.Line"/> to be added.</param>
  /// <returns>True if the line was added, false otherwise.</returns>
  public bool TryAppendLineSafely(CurveArray curveArray, SOG.Line line)
  {
    if (IsLineTooShort(line))
    {
      // poc : logging "Some lines in the CurveArray where ignored due to being smaller than the allowed curve length."
      return false;
    }

    curveArray.Append(_lineConverter.Convert(line));
    return true;
  }

  private static double Length((double X, double Y, double Z) v) => Math.Sqrt((v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z));

  private static (double X, double Y, double Z) Scale((double X, double Y, double Z) v, double s) =>
    (v.X * s, v.Y * s, v.Z * s);

  private static (double X, double Y, double Z) Add(
    (double X, double Y, double Z) a,
    (double X, double Y, double Z) b
  ) => (a.X + b.X, a.Y + b.Y, a.Z + b.Z);

  private static (double X, double Y, double Z) Sub(
    (double X, double Y, double Z) a,
    (double X, double Y, double Z) b
  ) => (a.X - b.X, a.Y - b.Y, a.Z - b.Z);

  private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
    (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

  private static (double X, double Y, double Z) Cross(
    (double X, double Y, double Z) a,
    (double X, double Y, double Z) b
  ) => ((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));

  private static (double X, double Y, double Z) Normalize((double X, double Y, double Z) v)
  {
    double length = Length(v);
    return length < 1e-9 ? (0, 0, 0) : Scale(v, 1 / length);
  }

  private static SOG.Point ToPoint((double X, double Y, double Z) v, string units) => new(v.X, v.Y, v.Z, units);

  private static SOG.Vector ToVector((double X, double Y, double Z) v, string units) => new(v.X, v.Y, v.Z, units);
}
