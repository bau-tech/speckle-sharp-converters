using System.Globalization;
using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Sdk.Common;
using Speckle.Sdk.Models;

namespace Speckle.Converters.TeklaShared.ToSpeckle.Raw;

public class GridToSpeckleConverter : ITypedConverter<TSM.Grid, IEnumerable<Base>>
{
  private readonly IConverterSettingsStore<TeklaConversionSettings> _settingsStore;

  public GridToSpeckleConverter(IConverterSettingsStore<TeklaConversionSettings> settingsStore)
  {
    _settingsStore = settingsStore;
  }

  // Tekla's Grid.CoordinateX/Y/RadialGrid.RadialCoordinates are NOT lists of absolute positions:
  // only the first value is an offset from Origin - every value after that (including each expansion
  // of an "N*value" repeat run) is the spacing from the PREVIOUS line, cumulative (confirmed against
  // Tekla's own grid dialog, e.g. "0.00 5*7200.00" places lines at 0, 7200, 14400, ...). Both the
  // plain and repeat-run branches below accumulate onto lastValue for this reason.
  private IEnumerable<double> ParseCoordinateString(string coordinateString)
  {
    if (string.IsNullOrEmpty(coordinateString))
    {
      yield break;
    }

    var numberStyles = NumberStyles.Float;
    var culture = CultureInfo.InvariantCulture;

    var parts = coordinateString.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    double lastValue = 0;

    foreach (var part in parts)
    {
      if (part.Contains("*"))
      {
        var repetitionParts = part.Split(new[] { '*' }, StringSplitOptions.RemoveEmptyEntries);
        if (
          repetitionParts.Length == 2
          && int.TryParse(repetitionParts[0], numberStyles, culture, out int count)
          && double.TryParse(repetitionParts[1], numberStyles, culture, out double increment)
        )
        {
          for (int i = 0; i < count; i++)
          {
            lastValue += increment;
            yield return lastValue;
          }
        }
      }
      else
      {
        if (double.TryParse(part, numberStyles, culture, out double value))
        {
          lastValue += value;
          yield return lastValue;
        }
      }
    }
  }

  public IEnumerable<Base> Convert(TSM.Grid target)
  {
    var coordinateSystem = target.GetCoordinateSystem();
    if (coordinateSystem == null)
    {
      yield break;
    }

    // Tekla's model is always internally millimeters, and CoordinateX/Y (once decoded from their
    // cumulative-delta encoding above) are distances from the coordinate system's Origin along its
    // local X/Y axes - add the Origin offset back in, then convert mm -> SpeckleUnits by a plain
    // multiply (no additional "scale" factor - Tekla doesn't apply one here).
    double conversionFactor = Units.GetConversionFactor(Units.Millimeters, _settingsStore.Current.SpeckleUnits);

    var xCoordinates = ParseCoordinateString(target.CoordinateX)
      .Select(x => (x + coordinateSystem.Origin.X) * conversionFactor)
      .ToList();
    var yCoordinates = ParseCoordinateString(target.CoordinateY)
      .Select(y => (y + coordinateSystem.Origin.Y) * conversionFactor)
      .ToList();

    double minX = xCoordinates.Min();
    double maxX = xCoordinates.Max();
    double minY = yCoordinates.Min();
    double maxY = yCoordinates.Max();

    double extendedMinX = minX - (target.ExtensionLeftX * conversionFactor);
    double extendedMaxX = maxX + (target.ExtensionRightX * conversionFactor);
    double extendedMinY = minY - (target.ExtensionLeftY * conversionFactor);
    double extendedMaxY = maxY + (target.ExtensionRightY * conversionFactor);

    double scaledZ = coordinateSystem.Origin.Z * conversionFactor;

    foreach (var x in xCoordinates)
    {
      var startPoint = new TG.Point(x, extendedMinY, scaledZ);
      var endPoint = new TG.Point(x, extendedMaxY, scaledZ);

      // we're using the Point converter indirectly through the Line converter
      // since we've already applied the conversion factor to the coordinates,
      // we need to tell the Point converter not to apply it again
      var line = new SOG.Line
      {
        start = new SOG.Point(startPoint.X, startPoint.Y, startPoint.Z, _settingsStore.Current.SpeckleUnits),
        end = new SOG.Point(endPoint.X, endPoint.Y, endPoint.Z, _settingsStore.Current.SpeckleUnits),
        units = _settingsStore.Current.SpeckleUnits,
      };

      yield return line;
    }

    foreach (var y in yCoordinates)
    {
      var startPoint = new TG.Point(extendedMinX, y, scaledZ);
      var endPoint = new TG.Point(extendedMaxX, y, scaledZ);

      var line = new SOG.Line
      {
        start = new SOG.Point(startPoint.X, startPoint.Y, startPoint.Z, _settingsStore.Current.SpeckleUnits),
        end = new SOG.Point(endPoint.X, endPoint.Y, endPoint.Z, _settingsStore.Current.SpeckleUnits),
        units = _settingsStore.Current.SpeckleUnits,
      };

      yield return line;
    }
  }
}
