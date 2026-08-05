using FluentAssertions;
using NUnit.Framework;
using Speckle.Converters.IfcShared.Extraction;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Tests;

public class IfcGeometryReadersTests
{
  private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-sample.ifc");

  [Test]
  public void TryReadCartesianPoint_MalformedNumericLiteral_ReturnsFalseRatherThanThrowing()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // #5030's first coordinate is "1.2.3" - the STEP tokenizer's number-char class (0-9, E, e, +, -,
    // .) happily consumes this as a single Number token, but double.Parse rejects it. Found from a
    // live Tekla receive: this uncaught FormatException propagated all the way up through
    // RevitNativeSchemaEnricher.EnrichFromFile and aborted enrichment for the ENTIRE file - every
    // object fell back to DirectShape, not just the one value that failed to parse. Must return
    // false, never throw - same "never guess, never crash" contract every other extractor here has.
    bool resolved = IfcGeometryReaders.TryReadCartesianPoint(graph, 5030, out _);

    resolved.Should().BeFalse();
  }
}
