using System.Globalization;
using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Objects.Data;

namespace Speckle.Converters.TeklaShared.ToHost.Ifc;

/// <summary>
/// Converts every IFC-enriched/synthesized grid-axis <see cref="DataObject"/> (builtInCategory
/// <c>OST_Grids</c> - see <c>RevitNativeSchemaEnricher.TryEnrichGrid</c>'s remarks: one axis reuses
/// the original <c>IfcGrid</c> object, every other axis is a synthesized sibling) into native Tekla
/// grid systems, merging axis-aligned straight lines into a single <see cref="TSM.Grid"/>. Mirrors
/// <see cref="RevitGridsToTeklaGridsConverter"/>, but simpler: <c>IfcGridExtractor</c> only ever
/// resolves straight 2-point <c>IfcGridAxis</c> curves (no arc/RadialGrid support exists on the IFC
/// side), so there is no radial-grid branch here.
/// </summary>
public class IfcGridsToTeklaGridsConverter
{
  // How far (in degrees) a line's direction may stray from true X/Y and still count as that axis.
  private const double AxisAlignmentToleranceDegrees = 3.0;

  private readonly ILogger<IfcGridsToTeklaGridsConverter> _logger;

  public IfcGridsToTeklaGridsConverter(ILogger<IfcGridsToTeklaGridsConverter> logger)
  {
    _logger = logger;
  }

  /// <summary>Result of converting one input grid axis: which output Tekla object (if any) it fed into.</summary>
  public readonly record struct GridConversionOutcome(DataObject Source, TSM.ModelObject? Result, string? Warning);

  public IReadOnlyList<GridConversionOutcome> Convert(IReadOnlyList<DataObject> gridObjects)
  {
    var outcomes = new List<GridConversionOutcome>(gridObjects.Count);
    if (gridObjects.Count == 0)
    {
      return outcomes;
    }

    var xLines = new List<(DataObject Source, double Coordinate, string Label)>();
    var yLines = new List<(DataObject Source, double Coordinate, string Label)>();
    double sharedZ = 0;
    bool haveZ = false;

    for (int i = 0; i < gridObjects.Count; i++)
    {
      DataObject target = gridObjects[i];
      string label = target.name.Length > 0 ? target.name : (i + 1).ToString(CultureInfo.InvariantCulture);

      if (target["location"] is not SOG.Line line)
      {
        outcomes.Add(
          new GridConversionOutcome(
            target,
            null,
            $"Grid '{label}' has an unsupported location type "
              + $"'{target["location"]?.GetType().Name ?? "null"}' for Tekla grid conversion - not converted."
          )
        );
        continue;
      }

      if (!haveZ)
      {
        sharedZ = line.start.z;
        haveZ = true;
      }

      double dx = line.end.x - line.start.x;
      double dy = line.end.y - line.start.y;
      double angleDeg = Math.Abs(Math.Atan2(dy, dx) * 180.0 / Math.PI); // 0..180

      if (Math.Abs(angleDeg - 90) <= AxisAlignmentToleranceDegrees)
      {
        double coordX = (line.start.x + line.end.x) / 2.0;
        xLines.Add((target, coordX, label));
      }
      else if (angleDeg <= AxisAlignmentToleranceDegrees || angleDeg >= 180 - AxisAlignmentToleranceDegrees)
      {
        double coordY = (line.start.y + line.end.y) / 2.0;
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
    }

    if (xLines.Count > 0 || yLines.Count > 0)
    {
      TSM.Grid cartesianGrid = BuildCartesianGrid(xLines, yLines, sharedZ);
      foreach (var (source, _, _) in xLines.Concat(yLines))
      {
        outcomes.Add(new GridConversionOutcome(source, cartesianGrid, null));
      }
    }

    return outcomes;
  }

  private TSM.Grid BuildCartesianGrid(
    List<(DataObject Source, double Coordinate, string Label)> xLines,
    List<(DataObject Source, double Coordinate, string Label)> yLines,
    double z
  )
  {
    xLines.Sort((a, b) => a.Coordinate.CompareTo(b.Coordinate));
    yLines.Sort((a, b) => a.Coordinate.CompareTo(b.Coordinate));

    // Tekla's Grid.CoordinateX/Y is NOT a list of absolute positions from Origin - only the first
    // value is (an offset from Origin); every value after that is the spacing from the previous
    // line, cumulative (see RevitGridsToTeklaGridsConverter.BuildCartesianGrid's remarks).
    string coordinateX = JoinCoordinates(ToAbsoluteThenDeltas(xLines.Select(l => l.Coordinate)));
    string coordinateY = JoinCoordinates(ToAbsoluteThenDeltas(yLines.Select(l => l.Coordinate)));
    string labelX = JoinLabels(xLines.Select(l => l.Label));
    string labelY = JoinLabels(yLines.Select(l => l.Label));
    _logger.LogInformation(
      "IfcGridsToTeklaGridsConverter.BuildCartesianGrid: Origin=(0,0,{Z}) CoordinateX='{CoordinateX}' LabelX='{LabelX}' CoordinateY='{CoordinateY}' LabelY='{LabelY}'",
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

  private static string JoinLabels(IEnumerable<string> labels) =>
    string.Join(" ", labels.Select(l => l.Replace(' ', '_')));
}
