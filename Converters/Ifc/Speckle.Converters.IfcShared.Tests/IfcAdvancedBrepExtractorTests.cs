using FluentAssertions;
using NUnit.Framework;
using Speckle.Converters.IfcShared.Extraction;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Tests;

public class IfcAdvancedBrepExtractorTests
{
  private const double TOLERANCE = 1e-9;
  private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-sample.ifc");

  [Test]
  public void TryReadLowestBodyVertex_AdvancedBrepBody_ResolvesLowestZVertexRegardlessOfOrder()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #3058's Body chains IfcAdvancedBrep -> IfcClosedShell -> IfcAdvancedFace -> IfcFaceOuterBound ->
    // IfcEdgeLoop -> IfcOrientedEdge -> IfcEdgeCurve, whose two endpoints are (100,200,300) and
    // (400,500,100) - the LOWER-Z one (the edge's END vertex, not its start) must win, proving this
    // scans every vertex rather than just taking the first one found.
    bool resolved = IfcAdvancedBrepExtractor.TryReadLowestBodyVertex(graph, 3058, out var point);

    resolved.Should().BeTrue();
    point.X.Should().BeApproximately(400, TOLERANCE);
    point.Y.Should().BeApproximately(500, TOLERANCE);
    point.Z.Should().BeApproximately(100, TOLERANCE);
  }

  [Test]
  public void TryReadLowestBodyVertex_SweptSolidBody_ReturnsFalseRatherThanGuessing()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #506's Body is a SweptSolid, not an AdvancedBrep - must fail cleanly, not throw.
    bool resolved = IfcAdvancedBrepExtractor.TryReadLowestBodyVertex(graph, 506, out _);

    resolved.Should().BeFalse();
  }

  [Test]
  public void TryReadAllBodyVertices_FacetedBrepBody_ResolvesAllEightBoxCorners()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #3120 is a Tekla-style precast wall panel: an 8-corner box via IfcFacetedBrep/IfcFace/IfcPolyLoop
    // (no edge/vertex indirection at all, unlike the AdvancedBrep pile case) - confirmed to mirror a
    // real Tekla-authored panel's Body representation exactly.
    bool resolved = IfcAdvancedBrepExtractor.TryReadAllBodyVertices(graph, 3120, out var points);

    resolved.Should().BeTrue();

    double[] expectedXs = [0, 6000];
    double[] expectedYs = [-80, 80];
    double[] expectedZs = [-1800, 1800];
    foreach (double x in expectedXs)
    {
      foreach (double y in expectedYs)
      {
        foreach (double z in expectedZs)
        {
          points
            .Should()
            .Contain(p =>
              Math.Abs(p.X - x) < TOLERANCE && Math.Abs(p.Y - y) < TOLERANCE && Math.Abs(p.Z - z) < TOLERANCE
            );
        }
      }
    }
  }

  [Test]
  public void TryReadAllBodyVertices_PolygonalFaceSetBody_ResolvesAllVerticesFromSharedCoordinateList()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #4025 is a real ArchiCAD 26-authored beam's exact Body encoding: IfcPolygonalFaceSet over a
    // shared IfcCartesianPointList3D (IFC4's compact tessellation format) - no Axis representation at
    // all, and no AdvancedBrep/FacetedBrep wrapper either, so this is the ONLY usable geometry.
    bool resolved = IfcAdvancedBrepExtractor.TryReadAllBodyVertices(graph, 4025, out var points);

    resolved.Should().BeTrue();
    points.Should().HaveCount(3);
    points.Should().Contain(p => Math.Abs(p.X) < TOLERANCE && Math.Abs(p.Y) < TOLERANCE && Math.Abs(p.Z) < TOLERANCE);
    points
      .Should()
      .Contain(p => Math.Abs(p.X - 6000) < TOLERANCE && Math.Abs(p.Y) < TOLERANCE && Math.Abs(p.Z + 1200) < TOLERANCE);
    points
      .Should()
      .Contain(p => Math.Abs(p.X) < TOLERANCE && Math.Abs(p.Y - 400) < TOLERANCE && Math.Abs(p.Z) < TOLERANCE);
  }

  [Test]
  public void TryReadAllBodyFaces_PolygonalFaceSetBox_ResolvesSixOrderedQuadFaces()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #5010 mirrors a real ArchiCAD-authored slab's exact mesh shape: a simple 6-quad-face box (1 top
    // + 1 bottom + 4 sides), confirmed against the real file entity-for-entity (face #5005 is the top,
    // at constant Z=0, in the same point order the real file's own top face used).
    bool resolved = IfcAdvancedBrepExtractor.TryReadAllBodyFaces(graph, 5010, out var faces);

    resolved.Should().BeTrue();
    faces.Should().HaveCount(6);
    faces.Should().OnlyContain(face => face.Count == 4);

    // The top face (#5005, in the file) is (5,1,4,8) -> (0,3500,0),(0,0,0),(3500,0,0),(3500,3500,0).
    var topFace = faces.Single(face => face.All(p => Math.Abs(p.Z) < TOLERANCE));
    topFace.Should().HaveCount(4);
    topFace[0].X.Should().BeApproximately(0, TOLERANCE);
    topFace[0].Y.Should().BeApproximately(3500, TOLERANCE);
    topFace[2].X.Should().BeApproximately(3500, TOLERANCE);
    topFace[2].Y.Should().BeApproximately(0, TOLERANCE);
  }
}
