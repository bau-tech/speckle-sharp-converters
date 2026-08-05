using FluentAssertions;
using NUnit.Framework;
using Speckle.Converters.IfcShared.Extraction;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Tests;

/// <summary>
/// Manual verification only (not run in CI - requires a local file this repo doesn't ship): confirms
/// axis extraction against the real beam (#2935) in the full-size (~5.6MB) file, whose Axis goes
/// through the same MappedRepresentation indirection as the round column's Body.
/// </summary>
[Explicit("Requires D:\\RevitModels\\rstadvancedsampleproject.ifc locally - not a portable CI fixture.")]
public class IfcAxisExtractorRealFileTests
{
  [Test]
  public void TryExtractAxisLine_RealFile_BeamAxisResolvesViaMappedIndirection()
  {
    using var doc = new StepDocument(@"D:\RevitModels\rstadvancedsampleproject.ifc");
    var graph = StepGraph.Create(doc);

    bool resolved = IfcAxisExtractor.TryExtractAxisLine(graph, 2935, out var start, out var end);

    resolved.Should().BeTrue();
    // Start and end must be distinct points (a real baseline, not a degenerate zero-length line).
    (start.X != end.X || start.Y != end.Y || start.Z != end.Z)
      .Should()
      .BeTrue();
  }
}
