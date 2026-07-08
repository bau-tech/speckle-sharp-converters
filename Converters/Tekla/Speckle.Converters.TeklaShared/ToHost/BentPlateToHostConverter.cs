using Speckle.Converters.Common.Objects;
using Speckle.Objects.Data;
using Speckle.Sdk.Common.Exceptions;

namespace Speckle.Converters.TeklaShared.ToHost;

public class BentPlateToHostConverter : ITypedConverter<TeklaObject, TSM.BentPlate>
{
  public TSM.BentPlate Convert(TeklaObject target)
  {
    // BentPlate.Geometry is a ConnectiveGeometry — an alternating sequence of leg contours
    // (PolygonNode) and bend surfaces (BendSurfaceNode/Cylindrical|ConicalSurface), captured
    // verbatim on send as `geometry_sections`. Rebuild the same sequence here via
    // BentPlateGeometrySolver.AddLeg, which is the only supported way to grow a ConnectiveGeometry.
    if (
      !target.properties.TryGetValue("geometry_sections", out var sectionsObj)
      || sectionsObj is not IEnumerable<object> sectionsEnum
    )
    {
      throw new ConversionException(
        "BentPlate reconstruction failed — no geometry_sections captured on send for this object."
      );
    }

    var legs = new List<TSM.Contour>();
    var bends = new List<(string Shape, double Radius1, double Radius2)>();

    foreach (var raw in sectionsEnum)
    {
      if (raw is not IDictionary<string, object> section)
      {
        continue;
      }

      var kind = section.TryGetValue("kind", out var k) ? k?.ToString() : null;
      if (kind == "leg")
      {
        var contour = new TSM.Contour();
        if (section.TryGetValue("contour_points", out var ptsObj) && ptsObj is IEnumerable<object> ptsEnum)
        {
          var coords = ptsEnum.Select(System.Convert.ToDouble).ToList();
          for (int i = 0; i * 3 + 2 < coords.Count; i++)
          {
            contour.AddContourPoint(
              new TSM.ContourPoint(new TG.Point(coords[i * 3], coords[i * 3 + 1], coords[i * 3 + 2]), null)
            );
          }
        }
        legs.Add(contour);
      }
      else if (kind == "bend")
      {
        var shape = section.TryGetValue("bend_shape", out var bs) ? bs?.ToString() ?? "Cylindrical" : "Cylindrical";
        double r1 = section.TryGetValue("radius", out var r) && r != null ? System.Convert.ToDouble(r) : 0d;
        double r2 = 0d;
        if (shape == "Conical")
        {
          r1 = section.TryGetValue("radius1", out var cr1) && cr1 != null ? System.Convert.ToDouble(cr1) : r1;
          r2 = section.TryGetValue("radius2", out var cr2) && cr2 != null ? System.Convert.ToDouble(cr2) : 0d;
        }
        bends.Add((shape, r1, r2));
      }
    }

    if (legs.Count == 0)
    {
      throw new ConversionException("BentPlate reconstruction failed — captured geometry has no leg contours.");
    }

    var solver = new TSM.BentPlateGeometrySolver();
    TSM.ConnectiveGeometry geometry = new(legs[0]);
    for (int i = 1; i < legs.Count; i++)
    {
      (string Shape, double Radius1, double Radius2) bend =
        i - 1 < bends.Count ? bends[i - 1] : ("Cylindrical", 0d, 0d);
      geometry =
        bend.Shape == "Conical"
          ? solver.AddLeg(geometry, legs[i], bend.Radius1, bend.Radius2)
          : solver.AddLeg(geometry, legs[i], bend.Radius1);
    }

    var bentPlate = new TSM.BentPlate { Geometry = geometry };

    // Thickness is read-only on BentPlate — it's derived from Profile, which
    // TeklaPartPropertyApplicator.Apply (below) sets from the captured `profile` string.
    // Applies shared Part properties (profile, material, position, numbering, phase, UDAs) —
    // same as every other Part-derived converter (ContourPlate, PolyBeam, etc.). The captured
    // `profile` string is informational for BentPlate (its shape comes from Geometry, not
    // Profile), Tekla simply ignores/stores it without affecting the bent shape.
    TeklaPartPropertyApplicator.Apply(bentPlate, target);

    bentPlate.Insert();
    return bentPlate;
  }

  public object Convert(object target) => Convert((TeklaObject)target);
}
