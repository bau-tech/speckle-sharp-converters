using FluentAssertions;
using NUnit.Framework;
using Speckle.Converters.IfcShared.Extraction;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Tests;

/// <summary>
/// End-to-end tests (real STEP parsing + graph construction, not just the pure-math layer in
/// <see cref="IfcPlacementTests"/>) for <see cref="IfcPlacementResolver"/>, using the fixture's
/// synthetic rotated placement chain (express id #167, the round column's ObjectPlacement).
/// </summary>
public class IfcPlacementResolverTests
{
  private const double TOLERANCE = 1e-9;
  private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-sample.ifc");

  [Test]
  public void TryResolveAbsolutePlacement_RotatedChain_MatchesHandComputedResult()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    bool ok = IfcPlacementResolver.TryResolveAbsolutePlacement(graph, 167, out var placement);

    ok.Should().BeTrue();
    placement.Should().NotBeNull();
    placement!.Origin.X.Should().BeApproximately(100, TOLERANCE);
    placement.Origin.Y.Should().BeApproximately(210, TOLERANCE);
    placement.Origin.Z.Should().BeApproximately(0, TOLERANCE);
    placement.XAxis.X.Should().BeApproximately(0, TOLERANCE);
    placement.XAxis.Y.Should().BeApproximately(1, TOLERANCE);
  }

  [Test]
  public void TryResolveAbsolutePlacement_IdentityRootPlacement_ResolvesToOrigin()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #101 is a root IfcLocalPlacement (no PlacementRelTo) at the world origin.
    bool ok = IfcPlacementResolver.TryResolveAbsolutePlacement(graph, 101, out var placement);

    ok.Should().BeTrue();
    placement.Should().NotBeNull();
    placement!.Origin.X.Should().BeApproximately(0, TOLERANCE);
    placement.Origin.Y.Should().BeApproximately(0, TOLERANCE);
  }

  [Test]
  public void TryResolveAbsolutePlacement_UnknownId_ReturnsFalse()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    bool ok = IfcPlacementResolver.TryResolveAbsolutePlacement(graph, 999999, out var placement);

    ok.Should().BeFalse();
    placement.Should().BeNull();
  }
}
