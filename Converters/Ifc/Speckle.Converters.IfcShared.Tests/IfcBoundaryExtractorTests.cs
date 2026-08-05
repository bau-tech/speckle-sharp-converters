using FluentAssertions;
using NUnit.Framework;
using Speckle.Converters.IfcShared.Extraction;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Tests;

public class IfcBoundaryExtractorTests
{
  private const double TOLERANCE = 1e-9;
  private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-sample.ifc");

  [Test]
  public void TryExtractClosedBoundary_Floor_ResolvesFourCornerRectangle()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #930's FootPrint is a 4-segment IfcCompositeCurve (2-point IfcPolyline segments), mirroring the
    // real file's slab structure - a closed 5000x4000mm rectangle.
    bool resolved = IfcBoundaryExtractor.TryExtractClosedBoundary(graph, 930, out var points);

    resolved.Should().BeTrue();
    points.Should().HaveCount(4);
    points[0].X.Should().BeApproximately(0, TOLERANCE);
    points[0].Y.Should().BeApproximately(0, TOLERANCE);
    points[1].X.Should().BeApproximately(5000, TOLERANCE);
    points[1].Y.Should().BeApproximately(0, TOLERANCE);
    points[2].X.Should().BeApproximately(5000, TOLERANCE);
    points[2].Y.Should().BeApproximately(4000, TOLERANCE);
    points[3].X.Should().BeApproximately(0, TOLERANCE);
    points[3].Y.Should().BeApproximately(4000, TOLERANCE);
  }

  [Test]
  public void TryExtractClosedBoundary_FloorWithBarePolylineFootPrint_ResolvesFourCornerRectangleAndDropsClosingDuplicate()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #957's FootPrint is a bare closed IfcPolyline (no IfcCompositeCurve wrapper) with the first
    // point repeated as the last - the more common real-file case (3 of 5 slabs), found missing
    // entirely from a live receive (every floor fell to DirectShape, not just the curved one).
    bool resolved = IfcBoundaryExtractor.TryExtractClosedBoundary(graph, 957, out var points);

    resolved.Should().BeTrue();
    points.Should().HaveCount(4); // the repeated closing point must be dropped, not kept as a 5th
    points[0].X.Should().BeApproximately(0, TOLERANCE);
    points[0].Y.Should().BeApproximately(0, TOLERANCE);
    points[1].X.Should().BeApproximately(6000, TOLERANCE);
    points[2].Y.Should().BeApproximately(3000, TOLERANCE);
    points[3].X.Should().BeApproximately(0, TOLERANCE);
    points[3].Y.Should().BeApproximately(3000, TOLERANCE);
  }

  [Test]
  public void TryExtractClosedBoundary_RoundFloorWithArcSegments_TessellatesInsteadOfBailingOut()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #984's FootPrint is a full circle (radius 1000mm, centered at origin) built from 4 quarter-arc
    // IfcTrimmedCurve segments (.PARAMETER. trim, degrees) - regression coverage for a bug found from
    // a live receive: every curved floor fell to DirectShape even though tessellating the arc into a
    // chord-approximating polyline was preferred. 4 segments * ceil(90/2)=45 points each = 180 points.
    bool resolved = IfcBoundaryExtractor.TryExtractClosedBoundary(graph, 984, out var points);

    resolved.Should().BeTrue();
    points.Should().HaveCount(180);

    // First point: angle 0 on the circle.
    points[0].X.Should().BeApproximately(1000, TOLERANCE);
    points[0].Y.Should().BeApproximately(0, TOLERANCE);

    // Index 45: start of the second quarter-arc, angle 90 degrees.
    points[45].X.Should().BeApproximately(0, TOLERANCE);
    points[45].Y.Should().BeApproximately(1000, TOLERANCE);

    // Every tessellated point should lie on the circle (radius 1000 from the center).
    foreach (var point in points)
    {
      double distanceFromCenter = Math.Sqrt(point.X * point.X + point.Y * point.Y);
      distanceFromCenter.Should().BeApproximately(1000, TOLERANCE);
    }
  }

  [Test]
  public void TryExtractClosedBoundary_ElementWithNoFootPrintRepresentation_ReturnsFalse()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #168 (the round column) has 'Body'/'Axis' representations but no 'FootPrint' at all.
    bool resolved = IfcBoundaryExtractor.TryExtractClosedBoundary(graph, 168, out _);

    resolved.Should().BeFalse();
  }
}
