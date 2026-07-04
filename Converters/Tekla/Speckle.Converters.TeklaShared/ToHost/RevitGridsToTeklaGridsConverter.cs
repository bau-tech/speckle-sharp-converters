using System.Globalization;
using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Converters.TeklaShared.Helpers;
using Speckle.Objects.Data;

namespace Speckle.Converters.TeklaShared.ToHost;

/// <summary>
/// Converts every received Revit Grid (a <see cref="RevitObject"/> with builtInCategory
/// "OST_Grids", location a line or arc) into native Tekla grid systems: straight, axis-aligned
/// lines are merged into a single <see cref="TSM.Grid"/> (Tekla's cartesian grid holds the whole
/// set of X/Y coordinate lines as one object); arc lines sharing a common center are merged into
/// one <see cref="TSM.RadialGrid"/> per center. Unlike the other Revit→Tekla converters this
/// operates on the whole batch of grid objects at once, since a single Revit Grid line has no
/// standalone Tekla equivalent - it only makes sense as one entry in a shared grid system.
/// </summary>
public class RevitGridsToTeklaGridsConverter
{
  // How far (in degrees) a line's direction may stray from true X/Y and still count as that axis.
  // Revit projects commonly rotate "true north" rather than the project/grid system specifically
  // so the grid stays axis-aligned in project (and therefore shared/model) coordinates - exact
  // axis alignment is the common case, not the exception. Lines outside this tolerance are
  // reported and skipped rather than guessed at, since Tekla's Grid has no per-line rotation.
  private const double AxisAlignmentToleranceDegrees = 3.0;

  // Arc grid lines within this radius (mm) of each other's center are treated as the same circle
  // and merged into one RadialGrid.
  private const double CenterGroupingToleranceMm = 1.0;

  private readonly ITypedConverter<SOG.Point, TG.Point> _pointConverter;
  private readonly IConverterSettingsStore<TeklaConversionSettings> _settingsStore;

  public RevitGridsToTeklaGridsConverter(
    ITypedConverter<SOG.Point, TG.Point> pointConverter,
    IConverterSettingsStore<TeklaConversionSettings> settingsStore
  )
  {
    _pointConverter = pointConverter;
    _settingsStore = settingsStore;
  }

  /// <summary>Result of converting one input grid: which output Tekla object (if any) it fed into.</summary>
  public readonly record struct GridConversionOutcome(RevitObject Source, TSM.ModelObject? Result, string? Warning);

  public IReadOnlyList<GridConversionOutcome> Convert(IReadOnlyList<RevitObject> gridObjects)
  {
    var outcomes = new List<GridConversionOutcome>(gridObjects.Count);
    if (gridObjects.Count == 0)
    {
      return outcomes;
    }

    var xLines = new List<(RevitObject Source, double Coordinate, string Label)>();
    var yLines = new List<(RevitObject Source, double Coordinate, string Label)>();
    var arcGroups = new List<(TG.Point Center, List<(RevitObject Source, double Radius, string Label)> Entries)>();
    double sharedZ = 0;
    bool haveZ = false;

    for (int i = 0; i < gridObjects.Count; i++)
    {
      RevitObject target = gridObjects[i];
      double scale = RevitPropertyReader.GetUnitScaleFactor(target.units, _settingsStore.Current.SpeckleUnits);
      string label = target.name.Length > 0 ? target.name : (i + 1).ToString(CultureInfo.InvariantCulture);

      switch (target["location"])
      {
        case SOG.Line line:
        {
          var scaled = RevitPropertyReader.ScaleLine(line, scale);
          if (!haveZ)
          {
            sharedZ = scaled.start.z;
            haveZ = true;
          }

          double dx = scaled.end.x - scaled.start.x;
          double dy = scaled.end.y - scaled.start.y;
          double angleDeg = Math.Abs(Math.Atan2(dy, dx) * 180.0 / Math.PI); // 0..180

          if (Math.Abs(angleDeg - 90) <= AxisAlignmentToleranceDegrees)
          {
            // runs along Y -> a fixed-X line -> one entry in Tekla's CoordinateX
            xLines.Add((target, (scaled.start.x + scaled.end.x) / 2.0, label));
          }
          else if (angleDeg <= AxisAlignmentToleranceDegrees || angleDeg >= 180 - AxisAlignmentToleranceDegrees)
          {
            // runs along X -> a fixed-Y line -> one entry in Tekla's CoordinateY
            yLines.Add((target, (scaled.start.y + scaled.end.y) / 2.0, label));
          }
          else
          {
            outcomes.Add(
              new GridConversionOutcome(
                target,
                null,
                $"Grid '{label}' is not aligned to the model X or Y axis (Tekla grids only support axis-aligned "
                  + "lines) - not converted."
              )
            );
          }
          break;
        }
        case SOG.Arc arc:
        {
          var scaledCenter = RevitPropertyReader.ScalePoint(arc.plane.origin, scale);
          double radius = arc.radius * scale;
          if (!haveZ)
          {
            sharedZ = scaledCenter.z;
            haveZ = true;
          }

          var group = arcGroups.FirstOrDefault(g =>
            Math.Abs(g.Center.X - scaledCenter.x) <= CenterGroupingToleranceMm
            && Math.Abs(g.Center.Y - scaledCenter.y) <= CenterGroupingToleranceMm
          );
          if (group.Entries is null)
          {
            group = (_pointConverter.Convert(scaledCenter), new List<(RevitObject, double, string)>());
            arcGroups.Add(group);
          }
          group.Entries.Add((target, radius, label));
          break;
        }
        default:
          outcomes.Add(
            new GridConversionOutcome(
              target,
              null,
              $"Grid '{label}' has an unsupported location type '{target.location?.GetType().Name ?? "null"}' for "
                + "Tekla grid conversion - not converted."
            )
          );
          break;
      }
    }

    if (xLines.Count > 0 || yLines.Count > 0)
    {
      TSM.Grid cartesianGrid = BuildCartesianGrid(xLines, yLines, sharedZ);
      foreach (var (source, _, _) in xLines.Concat(yLines))
      {
        outcomes.Add(new GridConversionOutcome(source, cartesianGrid, null));
      }
    }

    foreach (var (center, entries) in arcGroups)
    {
      TSM.RadialGrid radialGrid = BuildRadialGrid(center, entries);
      foreach (var (source, _, _) in entries)
      {
        outcomes.Add(new GridConversionOutcome(source, radialGrid, null));
      }
    }

    return outcomes;
  }

  private static TSM.Grid BuildCartesianGrid(
    List<(RevitObject Source, double Coordinate, string Label)> xLines,
    List<(RevitObject Source, double Coordinate, string Label)> yLines,
    double z
  )
  {
    xLines.Sort((a, b) => a.Coordinate.CompareTo(b.Coordinate));
    yLines.Sort((a, b) => a.Coordinate.CompareTo(b.Coordinate));

    var grid = new TSM.Grid
    {
      Origin = new TG.Point(0, 0, z),
      CoordinateX = JoinCoordinates(xLines.Select(l => l.Coordinate)),
      CoordinateY = JoinCoordinates(yLines.Select(l => l.Coordinate)),
      CoordinateZ = "0",
      LabelX = JoinLabels(xLines.Select(l => l.Label)),
      LabelY = JoinLabels(yLines.Select(l => l.Label)),
    };
    grid.Insert();
    return grid;
  }

  private static TSM.RadialGrid BuildRadialGrid(
    TG.Point center,
    List<(RevitObject Source, double Radius, string Label)> entries
  )
  {
    entries.Sort((a, b) => a.Radius.CompareTo(b.Radius));

    var grid = new TSM.RadialGrid
    {
      Origin = center,
      RadialCoordinates = JoinCoordinates(entries.Select(e => e.Radius)),
      RadialLabels = JoinLabels(entries.Select(e => e.Label)),
    };
    grid.Insert();
    return grid;
  }

  private static string JoinCoordinates(IEnumerable<double> values) =>
    string.Join(" ", values.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));

  // Tekla's Label strings are space-delimited, positionally matched to the coordinate list -
  // sanitize whitespace out of names so a spaced Revit grid name can't desync the pairing.
  private static string JoinLabels(IEnumerable<string> labels) =>
    string.Join(" ", labels.Select(l => l.Replace(' ', '_')));
}
