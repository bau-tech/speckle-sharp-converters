using FluentAssertions;
using NUnit.Framework;
using Speckle.Converters.IfcShared.Extraction;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Tests;

public class IfcBodyBoundingBoxAxisHeuristicTests
{
  private const double TOLERANCE = 1e-9;
  private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-sample.ifc");

  [Test]
  public void TryComputeAxisFromBodyBoundingBox_ElongatedPanelBox_PicksDominantLengthAxis()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #3120's box spans X:[0,6000] (length), Y:[-80,80] (thickness), Z:[-1800,1800] (height) - X is
    // clearly dominant, so the axis should run along X at the midpoint of Y/Z (0, 0).
    bool resolved = IfcBodyBoundingBoxAxisHeuristic.TryComputeAxisFromBodyBoundingBox(
      graph,
      3120,
      out var start,
      out var end
    );

    resolved.Should().BeTrue();
    start.X.Should().BeApproximately(0, TOLERANCE);
    start.Y.Should().BeApproximately(0, TOLERANCE);
    start.Z.Should().BeApproximately(0, TOLERANCE);
    end.X.Should().BeApproximately(6000, TOLERANCE);
    end.Y.Should().BeApproximately(0, TOLERANCE);
    end.Z.Should().BeApproximately(0, TOLERANCE);
  }

  [Test]
  public void TryComputeAxisFromBodyBoundingBox_NoRecognizedBody_ReturnsFalseRatherThanGuessing()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #506's Body is a SweptSolid, not an AdvancedBrep/FacetedBrep - no vertices to derive a box from.
    bool resolved = IfcBodyBoundingBoxAxisHeuristic.TryComputeAxisFromBodyBoundingBox(graph, 506, out _, out _);

    resolved.Should().BeFalse();
  }
}
