using FluentAssertions;
using NUnit.Framework;
using Speckle.Converters.IfcShared.Extraction;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Tests;

public class IfcOpeningExtractorTests
{
  private const double TOLERANCE = 1e-6;
  private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-sample.ifc");

  [Test]
  public void BuildOpeningToHostIndex_ResolvesFloor930AsHostOfOpening968()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    var index = IfcOpeningHostExtractor.BuildOpeningToHostIndex(graph);

    index.Should().ContainKey(968u).WhoseValue.Should().Be(930u);
  }

  [Test]
  public void TryExtractBoundary_ComposesElementExtrusionAndProfilePositions()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #968's profile Position (#962) is offset to (1000,500) and rotated 90 degrees (RefDirection
    // (0,1)) relative to its extrusion - both element placement (#101) and extrusion Position (#964)
    // are identity, so the resolved boundary must reflect the profile Position's offset/rotation
    // alone. Hand-computed expected corners (400x200 rectangle, rotated 90deg, centered at
    // (1000,500)): X in [900,1100], Y in [300,700].
    bool resolved = IfcOpeningExtractor.TryExtractBoundary(graph, 968, out var points);

    resolved.Should().BeTrue();
    points.Should().HaveCount(4);

    double minX = points.Min(p => p.X);
    double maxX = points.Max(p => p.X);
    double minY = points.Min(p => p.Y);
    double maxY = points.Max(p => p.Y);

    minX.Should().BeApproximately(900, TOLERANCE);
    maxX.Should().BeApproximately(1100, TOLERANCE);
    minY.Should().BeApproximately(300, TOLERANCE);
    maxY.Should().BeApproximately(700, TOLERANCE);
  }

  [Test]
  public void TryExtractBoundary_ElementWithNoBodyRepresentation_ReturnsFalse()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #2467 (the wall) has an 'Axis' representation but no extruded 'Body'.
    bool resolved = IfcOpeningExtractor.TryExtractBoundary(graph, 2467, out _);

    resolved.Should().BeFalse();
  }
}
