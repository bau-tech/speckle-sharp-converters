using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Speckle.Converters.IfcShared.BlobRetrieval;

namespace Speckle.Converters.IfcShared.Tests;

public class IfcGlobalIdScannerTests
{
  private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-sample.ifc");

  [Test]
  public void ScanGlobalIds_FindsGlobalIdsOnIfcRootEntities()
  {
    var scanner = new IfcGlobalIdScanner(NullLogger<IfcGlobalIdScanner>.Instance);

    var result = scanner.ScanGlobalIds(FixturePath);

    // Wall and Column GlobalIds, taken from a real Revit-exported IFC4 file (rstadvancedsampleproject.ifc),
    // must be found and mapped to their correct express ids.
    result.Should().ContainKey("01SfNHv5nEReC9M9Bzo7D_").WhoseValue.Should().Be(2467u);
    result.Should().ContainKey("18YHwga450Mw4Fy6M5t_BM").WhoseValue.Should().Be(168u);
  }

  [Test]
  public void ScanGlobalIds_DoesNotFalsePositiveOnNonRootStringAttributes()
  {
    var scanner = new IfcGlobalIdScanner(NullLogger<IfcGlobalIdScanner>.Instance);

    var result = scanner.ScanGlobalIds(FixturePath);

    // IfcMaterial's name ("Concrete - Cast-in-Place Concrete - 28 MPa") is a leading string
    // attribute too, but is not 22 characters - must not be mistaken for a GlobalId.
    result.Values.Should().NotContain(81u);
  }

  [Test]
  public void ScanGlobalIds_SkipsEntitiesWhoseFirstAttributeIsNotAString()
  {
    var scanner = new IfcGlobalIdScanner(NullLogger<IfcGlobalIdScanner>.Instance);

    var result = scanner.ScanGlobalIds(FixturePath);

    // IfcCartesianPoint (#4, #2444, #64) starts with a coordinate list, not a string - must be skipped.
    result.Values.Should().NotContain(4u);
    result.Values.Should().NotContain(2444u);
    result.Values.Should().NotContain(64u);
  }

  /// <summary>
  /// Manual verification only (not run in CI - requires a local file this repo doesn't ship): confirms
  /// the scanner holds up against a real, full-size (~5.6MB) Revit-exported IFC4 file, not just the
  /// trimmed fixture above. Run explicitly via
  /// <c>dotnet test --filter "ScanGlobalIds_RealFile_FindsAllSixWalls"</c>.
  /// </summary>
  [Test]
  [Explicit("Requires D:\\RevitModels\\rstadvancedsampleproject.ifc locally - not a portable CI fixture.")]
  public void ScanGlobalIds_RealFile_FindsAllSixWalls()
  {
    var scanner = new IfcGlobalIdScanner(NullLogger<IfcGlobalIdScanner>.Instance);

    var result = scanner.ScanGlobalIds(@"D:\RevitModels\rstadvancedsampleproject.ifc");

    string[] knownWallGlobalIds =
    [
      "01SfNHv5nEReC9M9Bzo7D_",
      "01SfNHv5nEReC9M9Bzo7RE",
      "01SfNHv5nEReC9M9Bzo7PL",
      "01SfNHv5nEReC9M9Bzo7Oy",
      "3Cq0759frBlvbPwD18w2Gb",
      "3Cq0759frBlvbPwD18w2KL",
    ];
    foreach (string wallId in knownWallGlobalIds)
    {
      result.Should().ContainKey(wallId);
    }
  }
}
