using FluentAssertions;
using NUnit.Framework;
using Speckle.Converters.IfcShared.Extraction;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Tests;

public class IfcGridExtractorTests
{
  private const double TOLERANCE = 1e-9;
  private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-sample.ifc");

  [Test]
  public void TryExtractAxes_GridWithOneUAxisAndOneVAxis_ReturnsBothAsNamedLines()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #3038 has one UAxis ("1", a 1000mm-long polyline along X) and one VAxis ("A", a 2000mm-long
    // polyline along Y) - mirrors the real file's grid axis shape (simple 2-point IfcPolyline).
    bool resolved = IfcGridExtractor.TryExtractAxes(graph, 3038, out var axes);

    resolved.Should().BeTrue();
    axes.Should().HaveCount(2);

    var uAxis = axes.Single(a => a.Tag == "1");
    uAxis.Start.X.Should().BeApproximately(0, TOLERANCE);
    uAxis.Start.Y.Should().BeApproximately(0, TOLERANCE);
    uAxis.End.X.Should().BeApproximately(1000, TOLERANCE);
    uAxis.End.Y.Should().BeApproximately(0, TOLERANCE);

    var vAxis = axes.Single(a => a.Tag == "A");
    vAxis.Start.X.Should().BeApproximately(0, TOLERANCE);
    vAxis.Start.Y.Should().BeApproximately(0, TOLERANCE);
    vAxis.End.X.Should().BeApproximately(0, TOLERANCE);
    vAxis.End.Y.Should().BeApproximately(2000, TOLERANCE);
  }

  [Test]
  public void TryExtractAxes_NonGridEntity_ReturnsFalse()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #168 is the round column - has no UAxes/VAxes attributes at all, must fail cleanly, not throw.
    bool resolved = IfcGridExtractor.TryExtractAxes(graph, 168, out var axes);

    resolved.Should().BeFalse();
    axes.Should().BeEmpty();
  }
}
