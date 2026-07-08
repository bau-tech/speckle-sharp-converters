using System.Globalization;
using Speckle.Converters.RevitShared.Helpers;
using Speckle.Objects.Data;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.RevitShared.ToHost;

/// <summary>
/// Reconstructs a whole Tekla Cartesian grid *system* (one <see cref="TeklaObject"/>, type "Grid")
/// into N native <c>DB.Grid</c> elements - one per decoded X/Y coordinate. Tekla represents a whole
/// grid as a single part; Revit represents each grid line as its own element, so this is a
/// deliberate one-to-many fan-out, not a normal 1:1 converter (see <see cref="RevitRootToHostConverter"/>'s
/// dedicated "Grid" dispatch branch and the <c>GridSystemWrapper</c> result type it returns).
///
/// The real reconstruction data lives in raw properties captured on send by Tekla's
/// ClassPropertyExtractor.AddGridProperties - "coordinate_x"/"coordinate_y" are cumulative-delta
/// encoded strings (e.g. "0.00 5*7200.00" = lines at 0, 7200, 14400, ...; the decoder below ports
/// Tekla-side GridToSpeckleConverter.ParseCoordinateString's algorithm, which can't be shared
/// cross-assembly), "label_x"/"label_y" are raw label strings paired with the decoded coordinates
/// by index on a best-effort basis (skipped entirely, letting Revit auto-number, if the token count
/// doesn't match - the repeat-run label expansion rule isn't confirmed, so a wrong guess is worse
/// than no name at all).
/// </summary>
public class TeklaGridSystemToHostConverter
{
  private readonly GridToHostConverter _gridConverter;

  public TeklaGridSystemToHostConverter(GridToHostConverter gridConverter)
  {
    _gridConverter = gridConverter;
  }

  public IReadOnlyList<DB.Element> Convert(TeklaObject target)
  {
    var properties = target.properties;
    string units = target.units;

    List<double>? origin = GetDoubleList(properties.GetOrDefault("origin"));
    double originX = origin is { Count: >= 1 } ? origin[0] : 0;
    double originY = origin is { Count: >= 2 } ? origin[1] : 0;
    double originZ = origin is { Count: >= 3 } ? origin[2] : 0;

    double extLeftX = ToDoubleOrDefault(properties.GetOrDefault("extension_left_x"));
    double extRightX = ToDoubleOrDefault(properties.GetOrDefault("extension_right_x"));
    double extLeftY = ToDoubleOrDefault(properties.GetOrDefault("extension_left_y"));
    double extRightY = ToDoubleOrDefault(properties.GetOrDefault("extension_right_y"));

    List<double> xCoords = ParseCoordinateString(properties.GetOrDefault("coordinate_x") as string)
      .Select(x => x + originX)
      .ToList();
    List<double> yCoords = ParseCoordinateString(properties.GetOrDefault("coordinate_y") as string)
      .Select(y => y + originY)
      .ToList();

    if (xCoords.Count == 0 && yCoords.Count == 0)
    {
      throw new ConversionException("Tekla grid has no decodable X or Y coordinates.");
    }

    double minX = xCoords.Count > 0 ? xCoords.Min() : originX;
    double maxX = xCoords.Count > 0 ? xCoords.Max() : originX;
    double minY = yCoords.Count > 0 ? yCoords.Min() : originY;
    double maxY = yCoords.Count > 0 ? yCoords.Max() : originY;

    double extendedMinX = minX - extLeftX;
    double extendedMaxX = maxX + extRightX;
    double extendedMinY = minY - extLeftY;
    double extendedMaxY = maxY + extRightY;

    List<string>? xLabels = ParseLabels(properties.GetOrDefault("label_x") as string);
    List<string>? yLabels = ParseLabels(properties.GetOrDefault("label_y") as string);

    var grids = new List<DB.Element>();

    for (int i = 0; i < xCoords.Count; i++)
    {
      double x = xCoords[i];
      var line = new SOG.Line
      {
        start = new SOG.Point(x, extendedMinY, originZ, units),
        end = new SOG.Point(x, extendedMaxY, originZ, units),
        units = units,
      };
      string? name = xLabels is { } xl && xl.Count == xCoords.Count ? xl[i] : null;
      grids.Add(_gridConverter.Convert(BuildSyntheticGridBase(line, name)));
    }

    for (int i = 0; i < yCoords.Count; i++)
    {
      double y = yCoords[i];
      var line = new SOG.Line
      {
        start = new SOG.Point(extendedMinX, y, originZ, units),
        end = new SOG.Point(extendedMaxX, y, originZ, units),
        units = units,
      };
      string? name = yLabels is { } yl && yl.Count == yCoords.Count ? yl[i] : null;
      grids.Add(_gridConverter.Convert(BuildSyntheticGridBase(line, name)));
    }

    return grids;
  }

  private static Base BuildSyntheticGridBase(SOG.Line line, string? name)
  {
    var syntheticBase = new Base();
    syntheticBase["location"] = line;
    if (name is not null)
    {
      syntheticBase["name"] = name;
    }
    return syntheticBase;
  }

  // Ports Tekla-side GridToSpeckleConverter.ParseCoordinateString's algorithm (can't share
  // cross-assembly): Tekla's Grid.CoordinateX/Y are NOT lists of absolute positions - only the
  // first value is an offset from Origin, every value after that (including each expansion of an
  // "N*value" repeat run) is the spacing from the PREVIOUS line, cumulative.
  private static IEnumerable<double> ParseCoordinateString(string? coordinateString)
  {
    if (string.IsNullOrEmpty(coordinateString))
    {
      yield break;
    }

    string[] parts = coordinateString.Split([' '], StringSplitOptions.RemoveEmptyEntries);
    double lastValue = 0;

    foreach (string part in parts)
    {
      if (part.Contains('*'))
      {
        string[] repetitionParts = part.Split(['*'], StringSplitOptions.RemoveEmptyEntries);
        if (
          repetitionParts.Length == 2
          && int.TryParse(repetitionParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
          && double.TryParse(repetitionParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double increment)
        )
        {
          for (int i = 0; i < count; i++)
          {
            lastValue += increment;
            yield return lastValue;
          }
        }
      }
      else if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
      {
        lastValue += value;
        yield return lastValue;
      }
    }
  }

  private static List<string>? ParseLabels(string? labelString) =>
    string.IsNullOrEmpty(labelString) ? null : labelString.Split([' '], StringSplitOptions.RemoveEmptyEntries).ToList();

  private static List<double>? GetDoubleList(object? value) =>
    value is IEnumerable<object> items ? items.Select(ToDoubleOrDefault).ToList() : null;

  private static double ToDoubleOrDefault(object? value) =>
    value switch
    {
      double d => d,
      float f => f,
      int i => i,
      long l => l,
      string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
      _ => 0,
    };
}
