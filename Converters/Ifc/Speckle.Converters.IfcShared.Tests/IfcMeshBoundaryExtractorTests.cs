using FluentAssertions;
using NUnit.Framework;
using Speckle.Converters.IfcShared.Extraction;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Tests;

public class IfcMeshBoundaryExtractorTests
{
  private const double TOLERANCE = 1e-9;
  private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-sample.ifc");

  [Test]
  public void TryExtractLargestHorizontalFaceBoundary_SimpleBoxMesh_ReturnsTopOrBottomRectangle()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #5010 mirrors a real ArchiCAD-exported (Reference View) floor slab: no 'FootPrint', no extrudable
    // profile, only a Tessellation/IfcPolygonalFaceSet Body - a simple 3500x3500x1000mm box. The top
    // and bottom faces are the only horizontal ones and have equal area (a tie, broken by whichever is
    // encountered first) - both describe the same 3500x3500 footprint, so the assertions hold either way.
    bool resolved = IfcMeshBoundaryExtractor.TryExtractLargestHorizontalFaceBoundary(graph, 5010, out var points);

    resolved.Should().BeTrue();
    points.Should().HaveCount(4);

    double minX = points.Min(p => p.X);
    double maxX = points.Max(p => p.X);
    double minY = points.Min(p => p.Y);
    double maxY = points.Max(p => p.Y);
    minX.Should().BeApproximately(0, TOLERANCE);
    maxX.Should().BeApproximately(3500, TOLERANCE);
    minY.Should().BeApproximately(0, TOLERANCE);
    maxY.Should().BeApproximately(3500, TOLERANCE);

    // Every returned point must lie on the SAME horizontal plane (either Z=0 or Z=-1000, not a mix).
    double firstZ = points[0].Z;
    points.Should().OnlyContain(p => Math.Abs(p.Z - firstZ) < TOLERANCE);
  }

  [Test]
  public void TryExtractLargestHorizontalFaceBoundary_AdvancedBrepBody_ReturnsFalseRatherThanGuessing()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #3058's Body is IfcAdvancedBrep (edge-based) - deliberately unsupported here (see
    // IfcAdvancedBrepExtractor.TryReadAllBodyFaces's remarks), must fail cleanly, not throw.
    bool resolved = IfcMeshBoundaryExtractor.TryExtractLargestHorizontalFaceBoundary(graph, 3058, out _);

    resolved.Should().BeFalse();
  }
}
