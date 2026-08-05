using FluentAssertions;
using NUnit.Framework;
using Speckle.Converters.IfcShared.Extraction;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Tests;

public class IfcAxisExtractorTests
{
  private const double TOLERANCE = 1e-9;
  private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-sample.ifc");

  [Test]
  public void TryExtractAxisLine_BeamWithAdvancedBrepBodyButCleanAxis_SucceedsOnAxisRegardlessOfBody()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #603's Body is AdvancedBrep (confirmed unrecoverable by IfcProfileExtractorTests), but its Axis
    // is a clean 2-point polyline - mirrors the real file exactly: mitered end cuts break Body, Axis
    // is unaffected.
    bool resolved = IfcAxisExtractor.TryExtractAxisLine(graph, 603, out var start, out var end);

    resolved.Should().BeTrue();
    start.X.Should().BeApproximately(0, TOLERANCE);
    start.Y.Should().BeApproximately(0, TOLERANCE);
    start.Z.Should().BeApproximately(3000, TOLERANCE);
    end.X.Should().BeApproximately(5000, TOLERANCE);
    end.Y.Should().BeApproximately(0, TOLERANCE);
    end.Z.Should().BeApproximately(3000, TOLERANCE);
  }

  [Test]
  public void TryExtractAxisLine_ColumnWithNoAxisRepresentation_ReturnsFalse()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #168 (the round column) only has a 'Body' representation, no 'Axis' - must fail cleanly, not throw.
    bool resolved = IfcAxisExtractor.TryExtractAxisLine(graph, 168, out _, out _);

    resolved.Should().BeFalse();
  }

  [Test]
  public void TryExtractAxisLine_StraightTrimmedCurveOverLine_Succeeds()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #715's Axis is an IfcTrimmedCurve over an IfcLine, .CARTESIAN. trim - mirrors the real file's
    // beam #2935 exactly (previously only covered by the explicit real-file test).
    bool resolved = IfcAxisExtractor.TryExtractAxisLine(graph, 715, out var start, out var end);

    resolved.Should().BeTrue();
    start.X.Should().BeApproximately(0, TOLERANCE);
    start.Y.Should().BeApproximately(0, TOLERANCE);
    start.Z.Should().BeApproximately(4000, TOLERANCE);
    end.X.Should().BeApproximately(3000, TOLERANCE);
    end.Y.Should().BeApproximately(0, TOLERANCE);
    end.Z.Should().BeApproximately(4000, TOLERANCE);
  }

  [Test]
  public void TryExtractAxisLine_CurvedTrimmedCurveOverCircle_ReturnsFalse()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // Regression test: #735's Axis is an IfcTrimmedCurve over an IfcCircle - the two .CARTESIAN. trim
    // points are only the chord endpoints of the arc, not a straight segment. Must bail out (false),
    // not silently return the chord as if it were a straight beam (the bug found from a live receive).
    bool resolved = IfcAxisExtractor.TryExtractAxisLine(graph, 735, out _, out _);

    resolved.Should().BeFalse();
  }

  [Test]
  public void TryExtractAxisLine_IndexedPolyCurveSingleLineSegment_Succeeds()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #4014's Axis is an IfcIndexedPolyCurve with a single IFCLINEINDEX((1,2)) segment over a shared
    // 2-point IfcCartesianPointList2D - confirmed to be a real ArchiCAD 26-authored wall's exact Axis
    // encoding (Body is a Tessellation mesh with no usable profile at all, unlike every other file this
    // feature was tested against - Axis is the ONLY usable geometry for these walls).
    bool resolved = IfcAxisExtractor.TryExtractAxisLine(graph, 4014, out var start, out var end);

    resolved.Should().BeTrue();
    start.X.Should().BeApproximately(0, TOLERANCE);
    start.Y.Should().BeApproximately(0, TOLERANCE);
    end.X.Should().BeApproximately(5259.675803822205, TOLERANCE);
    end.Y.Should().BeApproximately(0, TOLERANCE);
  }

  [Test]
  public void TryExtractAxisLine_IndexedPolyCurveArcSegment_ReturnsFalseRatherThanGuessing()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #5024's Axis is a single IFCARCINDEX segment - a curved wall - which the straight-line extractor
    // must reject (see TryExtractAxisArc for the dedicated curved-axis extractor).
    bool resolved = IfcAxisExtractor.TryExtractAxisLine(graph, 5024, out _, out _);

    resolved.Should().BeFalse();
  }

  [Test]
  public void TryExtractAxisArc_IndexedPolyCurveSingleArcSegment_ResolvesStartMidEnd()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #5024's Axis is an IfcIndexedPolyCurve with a single IFCARCINDEX((1,2,3)) segment over a shared
    // 3-point IfcCartesianPointList2D - confirmed common (~23% of walls) in a real ArchiCAD 26 export
    // (curved walls), not a rare edge case.
    bool resolved = IfcAxisExtractor.TryExtractAxisArc(graph, 5024, out var start, out var mid, out var end);

    resolved.Should().BeTrue();
    start.X.Should().BeApproximately(0, TOLERANCE);
    start.Y.Should().BeApproximately(0, TOLERANCE);
    mid.X.Should().BeApproximately(500, TOLERANCE);
    mid.Y.Should().BeApproximately(500, TOLERANCE);
    end.X.Should().BeApproximately(1000, TOLERANCE);
    end.Y.Should().BeApproximately(0, TOLERANCE);
  }

  [Test]
  public void TryExtractAxisArc_StraightLineSegment_ReturnsFalse()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #4014's Axis is a single IFCLINEINDEX segment - a straight wall - which the arc extractor must
    // reject rather than guessing at a 3rd point.
    bool resolved = IfcAxisExtractor.TryExtractAxisArc(graph, 4014, out _, out _, out _);

    resolved.Should().BeFalse();
  }
}
