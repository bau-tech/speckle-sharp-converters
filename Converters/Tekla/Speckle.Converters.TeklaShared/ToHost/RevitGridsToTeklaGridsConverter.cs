using System.Globalization;
using Microsoft.Extensions.Logging;
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
  private readonly ILogger<RevitGridsToTeklaGridsConverter> _logger;

  public RevitGridsToTeklaGridsConverter(
    ITypedConverter<SOG.Point, TG.Point> pointConverter,
    IConverterSettingsStore<TeklaConversionSettings> settingsStore,
    ILogger<RevitGridsToTeklaGridsConverter> logger
  )
  {
    _pointConverter = pointConverter;
    _settingsStore = settingsStore;
    _logger = logger;
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

          _logger.LogInformation(
            "  Grid '{Label}' raw=({RawSX},{RawSY})-({RawEX},{RawEY}) units={Units} scale={Scale} "
              + "scaled=({SX},{SY})-({EX},{EY}) angleDeg={AngleDeg}",
            label,
            line.start.x,
            line.start.y,
            line.end.x,
            line.end.y,
            target.units,
            scale,
            scaled.start.x,
            scaled.start.y,
            scaled.end.x,
            scaled.end.y,
            angleDeg
          );

          if (Math.Abs(angleDeg - 90) <= AxisAlignmentToleranceDegrees)
          {
            // runs along Y -> a fixed-X line -> one entry in Tekla's CoordinateX
            double coordX = (scaled.start.x + scaled.end.x) / 2.0;
            _logger.LogInformation("    -> X grid, coordinate={Coordinate}", coordX);
            xLines.Add((target, coordX, label));
          }
          else if (angleDeg <= AxisAlignmentToleranceDegrees || angleDeg >= 180 - AxisAlignmentToleranceDegrees)
          {
            // runs along X -> a fixed-Y line -> one entry in Tekla's CoordinateY
            double coordY = (scaled.start.y + scaled.end.y) / 2.0;
            _logger.LogInformation("    -> Y grid, coordinate={Coordinate}", coordY);
            yLines.Add((target, coordY, label));
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

  private TSM.Grid BuildCartesianGrid(
    List<(RevitObject Source, double Coordinate, string Label)> xLines,
    List<(RevitObject Source, double Coordinate, string Label)> yLines,
    double z
  )
  {
    xLines.Sort((a, b) => a.Coordinate.CompareTo(b.Coordinate));
    yLines.Sort((a, b) => a.Coordinate.CompareTo(b.Coordinate));

    // Tekla's Grid.CoordinateX/Y is NOT a list of absolute positions from Origin - only the first
    // value is (an offset from Origin); every value after that is the spacing from the PREVIOUS
    // line, cumulative (confirmed against Tekla's own grid dialog, e.g. "0.00 5*7200.00" places
    // lines at 0, 7200, 14400, ... not literally at 0 and 7200). Passing absolute positions for
    // every entry compounds into wildly wrong spacing beyond the second line.
    string coordinateX = JoinCoordinates(ToAbsoluteThenDeltas(xLines.Select(l => l.Coordinate)));
    string coordinateY = JoinCoordinates(ToAbsoluteThenDeltas(yLines.Select(l => l.Coordinate)));
    string labelX = JoinLabels(xLines.Select(l => l.Label));
    string labelY = JoinLabels(yLines.Select(l => l.Label));
    _logger.LogInformation(
      "  BuildCartesianGrid: Origin=(0,0,{Z}) CoordinateX='{CoordinateX}' LabelX='{LabelX}' CoordinateY='{CoordinateY}' LabelY='{LabelY}'",
      z,
      coordinateX,
      labelX,
      coordinateY,
      labelY
    );

    var grid = new TSM.Grid
    {
      Origin = new TG.Point(0, 0, z),
      CoordinateX = coordinateX,
      CoordinateY = coordinateY,
      CoordinateZ = "0",
      LabelX = labelX,
      LabelY = labelY,
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

    // Same cumulative-delta convention as Grid.CoordinateX/Y (see BuildCartesianGrid) - only the
    // first radius is absolute from Origin, every subsequent value is the spacing from the
    // previous ring.
    var grid = new TSM.RadialGrid
    {
      Origin = center,
      RadialCoordinates = JoinCoordinates(ToAbsoluteThenDeltas(entries.Select(e => e.Radius))),
      RadialLabels = JoinLabels(entries.Select(e => e.Label)),
    };
    grid.Insert();
    return grid;
  }

  // Converts a sequence already sorted ascending into "first value absolute, remaining values are
  // the delta from the previous entry" - the encoding Tekla's Grid.CoordinateX/Y and
  // RadialGrid.RadialCoordinates actually expect (confirmed against Tekla's own grid dialog).
  private static IEnumerable<double> ToAbsoluteThenDeltas(IEnumerable<double> sortedAscending)
  {
    double previous = 0;
    foreach (double value in sortedAscending)
    {
      yield return value - previous;
      previous = value;
    }
  }

  private static string JoinCoordinates(IEnumerable<double> values) =>
    string.Join(" ", values.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));

  // Tekla's Label strings are space-delimited, positionally matched to the coordinate list -
  // sanitize whitespace out of names so a spaced Revit grid name can't desync the pairing.
  private static string JoinLabels(IEnumerable<string> labels) =>
    string.Join(" ", labels.Select(l => l.Replace(' ', '_')));
}
