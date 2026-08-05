using FluentAssertions;
using NUnit.Framework;
using Speckle.Converters.TeklaShared.Helpers;

namespace Speckle.Converters.TeklaShared.Tests;

public class RevitPropertyReaderTests
{
  [TestCase("autodesk.unit.unit:millimeters-1.0.1", 200, 200)]
  [TestCase("autodesk.unit.unit:centimeters-1.0.1", 20, 200)]
  [TestCase("autodesk.unit.unit:meters-1.0.1", 0.2, 200)]
  // Revit's composite "Meters and Centimeters" project unit format for Length - textually contains
  // "centimeters", which previously caused a naive substring match to treat a meters-magnitude
  // value as centimeters (100x too small).
  [TestCase("autodesk.unit.unit:metersCentimeters-1.0.1", 0.2, 200)]
  [TestCase("autodesk.unit.unit:feet-1.0.1", 0.65616797900262467, 200)]
  [TestCase("autodesk.unit.unit:feetFractionalInches-1.0.1", 0.65616797900262467, 200)]
  [TestCase("autodesk.unit.unit:inches-1.0.1", 7.8740157480314959, 200)]
  [TestCase("autodesk.unit.unit:fractionalInches-1.0.1", 7.8740157480314959, 200)]
  [TestCase(null, 200, 200)]
  [TestCase("autodesk.unit.unit:unrecognized-1.0.1", 200, 200)]
  public void ConvertToMm_ScalesByExactUnitToken(string? unitsTypeId, double value, double expectedMm)
  {
    RevitPropertyReader.ConvertToMm(value, unitsTypeId).Should().BeApproximately(expectedMm, 1e-6);
  }
}
