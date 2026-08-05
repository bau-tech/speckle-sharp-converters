using FluentAssertions;
using NUnit.Framework;
using Speckle.Converters.IfcShared.Extraction;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Tests;

public class IfcProfileExtractorTests
{
  private const double TOLERANCE = 1e-6;
  private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-sample.ifc");

  [Test]
  public void TryResolveExtrudedProfile_ColumnWithMappedRepresentation_ResolvesCircleProfile()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #168 is the round column, whose Body goes through IfcMappedItem -> IfcRepresentationMap
    // indirection (the real structure found in rstadvancedsampleproject.ifc).
    bool resolved = IfcProfileExtractor.TryResolveExtrudedProfile(
      graph,
      168,
      out uint profileDefId,
      out double depthMm,
      out var localPosition
    );

    resolved.Should().BeTrue();
    depthMm.Should().BeApproximately(3500.0000000011578, TOLERANCE);
    // Fixture's extrusion Position is #67=IFCAXIS2PLACEMENT3D(#3,$,$) - identity, unlike the real
    // file's actual columns (see the RealFile test below), which is exactly why this needs its own
    // real-file regression coverage rather than trusting the fixture alone.
    localPosition.Origin.X.Should().BeApproximately(0, TOLERANCE);
    localPosition.Origin.Y.Should().BeApproximately(0, TOLERANCE);
    localPosition.Origin.Z.Should().BeApproximately(0, TOLERANCE);

    bool isCircle = IfcProfileExtractor.TryReadCircleProfile(graph, profileDefId, out double diameterMm);
    isCircle.Should().BeTrue();
    diameterMm.Should().BeApproximately(449.99999999999124, TOLERANCE);
  }

  [Test]
  public void TryResolveExtrudedProfile_WithDirectionOverload_ResolvesExtrudedDirection()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #3028's Body extrusion (#3025=IFCEXTRUDEDAREASOLID(#3022,#3024,#9,800.)) uses #9=IFCDIRECTION((0.,
    // 0.,1.)) as its ExtrudedDirection - added for the beam/wall axis-from-extrusion fallback (see
    // RevitNativeSchemaEnricher.TryResolveAxisWithFallbacks), which needs this direction to derive an
    // axis line when no 'Axis' representation exists at all.
    bool resolved = IfcProfileExtractor.TryResolveExtrudedProfile(
      graph,
      3028,
      out _,
      out _,
      out _,
      out var extrudedDirection
    );

    resolved.Should().BeTrue();
    extrudedDirection.X.Should().BeApproximately(0, TOLERANCE);
    extrudedDirection.Y.Should().BeApproximately(0, TOLERANCE);
    extrudedDirection.Z.Should().BeApproximately(1, TOLERANCE);
  }

  [Test]
  public void TryResolveExtrudedProfile_BeamWithDirectSweptSolid_ResolvesRectangleProfile()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #506 is a rectangular beam with a direct (non-Mapped) SweptSolid body.
    bool resolved = IfcProfileExtractor.TryResolveExtrudedProfile(
      graph,
      506,
      out uint profileDefId,
      out double depthMm,
      out _
    );

    resolved.Should().BeTrue();
    depthMm.Should().BeApproximately(6000, TOLERANCE);

    bool isRectangle = IfcProfileExtractor.TryReadRectangleProfile(
      graph,
      profileDefId,
      out double widthMm,
      out double heightMm
    );
    isRectangle.Should().BeTrue();
    widthMm.Should().BeApproximately(400, TOLERANCE);
    heightMm.Should().BeApproximately(800, TOLERANCE);
  }

  [Test]
  public void TryResolveBodyExtrusionDepth_WallWithClippingBody_UnwrapsToBaseExtrusionDepth()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #2467's Body is "Clipping" (IfcBooleanClippingResult wrapping a 3600mm IfcExtrudedAreaSolid) -
    // mirrors the real file's wall bodies exactly. TryResolveExtrudedProfile (SweptSolid-only) would
    // reject this; TryResolveBodyExtrusionDepth must unwrap the boolean to find the real height.
    bool resolved = IfcProfileExtractor.TryResolveBodyExtrusionDepth(graph, 2467, out double depthMm);

    resolved.Should().BeTrue();
    depthMm.Should().BeApproximately(3600, TOLERANCE);
  }

  [Test]
  public void TryResolveExtrudedProfile_AdvancedBrepBody_ReturnsFalseRatherThanGuessing()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #603 mirrors the real file's beams: Body is 'AdvancedBrep', not a clean extrusion - confirmed
    // in the live-file findings to actually occur, not a hypothetical edge case.
    bool resolved = IfcProfileExtractor.TryResolveExtrudedProfile(graph, 603, out _, out _, out _);

    resolved.Should().BeFalse();
  }

  [Test]
  public void TryReadArbitraryClosedProfileBoundary_RectangleWithOneRoundedCorner_TessellatesArcCorrectly()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #3082 mirrors a real Tekla-authored slab profile: IfcArbitraryClosedProfileDef over an
    // IfcIndexedPolyCurve with a shared IfcCartesianPointList2D and LINEINDEX/ARCINDEX segments - a
    // 500x500 square with one corner rounded by a clean 90deg, radius-100 fillet centered at (400,400)
    // (point list: (0,0),(500,0),(500,400),(470.71068,470.71068)=45deg,(400,500),(0,500)).
    bool resolved = IfcProfileExtractor.TryReadArbitraryClosedProfileBoundary(graph, 3082, out var points);

    resolved.Should().BeTrue();
    // 2 points from the first LINEINDEX + ~45 tessellated arc points (90deg / 2deg-per-vertex) + 2
    // points from the second LINEINDEX - each segment's own shared/closing point is left for whichever
    // segment comes next to supply, same convention as IfcBoundaryExtractor's composite-curve handling.
    // A tolerant range (rather than an exact count) avoids flakiness from the hand-typed 45deg fixture
    // point not being bit-for-bit exact, which can nudge the computed sweep a hair past a tessellation
    // step boundary and add/remove one segment.
    points.Count.Should().BeInRange(47, 51);

    points[0].X.Should().BeApproximately(0, TOLERANCE);
    points[0].Y.Should().BeApproximately(0, TOLERANCE);
    points[1].X.Should().BeApproximately(500, TOLERANCE);
    points[1].Y.Should().BeApproximately(0, TOLERANCE);

    // The arc's own start point (index 2) must exactly match where the fillet begins.
    points[2].X.Should().BeApproximately(500, TOLERANCE);
    points[2].Y.Should().BeApproximately(400, TOLERANCE);

    // The second LINEINDEX group's own start (the arc's end point, (400,500)) and the final point,
    // (0,500) - addressed relative to the end of the list so a +-1 segment-count difference doesn't
    // break this assertion.
    points[^2].X.Should().BeApproximately(400, 1e-3);
    points[^2].Y.Should().BeApproximately(500, 1e-3);
    points[^1].X.Should().BeApproximately(0, TOLERANCE);
    points[^1].Y.Should().BeApproximately(500, TOLERANCE);
  }

  [Test]
  public void TryReadArbitraryClosedProfileBoundary_PlainClosedPolyline_StripsDuplicateClosingPoint()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #3136 mirrors a real ArchiCAD-authored slab profile: IfcArbitraryClosedProfileDef over a plain
    // closed IfcPolyline (5 points, the first repeated as the last to close the loop) - NOT an
    // IfcIndexedPolyCurve, confirmed to be a different real exporter's convention for the exact same
    // "no separate FootPrint representation" gap.
    bool resolved = IfcProfileExtractor.TryReadArbitraryClosedProfileBoundary(graph, 3136, out var points);

    resolved.Should().BeTrue();
    points.Should().HaveCount(4);
    points[0].X.Should().BeApproximately(0, TOLERANCE);
    points[0].Y.Should().BeApproximately(0, TOLERANCE);
    points[1].X.Should().BeApproximately(2000, TOLERANCE);
    points[2].Y.Should().BeApproximately(3000, TOLERANCE);
    points[3].X.Should().BeApproximately(0, TOLERANCE);
    points[3].Y.Should().BeApproximately(3000, TOLERANCE);
  }

  [Test]
  public void TryReadArbitraryClosedProfileBoundary_RectangleProfile_ReturnsFalseRatherThanGuessing()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #65 is a plain IfcCircleProfileDef, not an IfcArbitraryClosedProfileDef at all - must fail
    // cleanly, not throw.
    bool resolved = IfcProfileExtractor.TryReadArbitraryClosedProfileBoundary(graph, 65, out _);

    resolved.Should().BeFalse();
  }

  /// <summary>
  /// Manual verification only (not run in CI - requires a local file this repo doesn't ship): confirms
  /// profile AND placement extraction against the actual round column (#168) in the real, full-size
  /// (~5.6MB) file, not just the hand-copied fixture. Run explicitly via
  /// <c>dotnet test --filter "TryResolveExtrudedProfile_RealFile_ColumnMatchesLiveFileFindings"</c>.
  /// </summary>
  [Test]
  [Explicit("Requires D:\\RevitModels\\rstadvancedsampleproject.ifc locally - not a portable CI fixture.")]
  public void TryResolveExtrudedProfile_RealFile_ColumnMatchesLiveFileFindings()
  {
    using var doc = new StepDocument(@"D:\RevitModels\rstadvancedsampleproject.ifc");
    var graph = StepGraph.Create(doc);

    bool resolved = IfcProfileExtractor.TryResolveExtrudedProfile(
      graph,
      168,
      out uint profileDefId,
      out double depthMm,
      out var localPosition
    );
    resolved.Should().BeTrue();
    depthMm.Should().BeApproximately(3500.0000000011578, TOLERANCE);

    bool isCircle = IfcProfileExtractor.TryReadCircleProfile(graph, profileDefId, out double diameterMm);
    isCircle.Should().BeTrue();
    diameterMm.Should().BeApproximately(449.99999999999124, TOLERANCE);

    bool placed = IfcPlacementResolver.TryResolveAbsolutePlacement(graph, 167, out var placement);
    placed.Should().BeTrue();
    placement.Should().NotBeNull();

    // Regression coverage for a bug found from a live receive: every instance of this repeated column
    // type shares the exact same (identity) ObjectPlacement AND IfcRepresentationMap.MappingOrigin in
    // this file - the real per-instance world offset lives entirely in the extrusion's own local
    // Position (#66=(-9201.999441595437, 29993.738619937787, 0.) for this specific column). If this
    // ever regresses back to identity, every column would collapse back onto the same world point.
    localPosition.Origin.X.Should().BeApproximately(-9201.999441595437, TOLERANCE);
    localPosition.Origin.Y.Should().BeApproximately(29993.738619937787, TOLERANCE);
    localPosition.Origin.Z.Should().BeApproximately(0, TOLERANCE);
  }
}
