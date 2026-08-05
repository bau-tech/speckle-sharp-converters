using FluentAssertions;
using NUnit.Framework;
using Speckle.Converters.IfcShared.Extraction;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Tests;

public class IfcUnitResolverTests
{
  private const double TOLERANCE = 1e-6;
  private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-sample.ifc");

  [Test]
  public void ResolveLengthScaleToMillimeters_PlainMetreUnitNoPrefix_Resolves1000()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);

    // The shared fixture's #4002 IfcProject -> #4001 IfcUnitAssignment -> #4000 IfcSIUnit(*,.LENGTHUNIT.,
    // $,.METRE.) - no MILLI prefix - mirrors a real ArchiCAD-authored file confirmed to use exactly this
    // (no conversion-based unit at all), unlike the millimetre-native Revit/Tekla files this feature was
    // originally built against.
    double scale = IfcUnitResolver.ResolveLengthScaleToMillimeters(graph);

    scale.Should().BeApproximately(1000, TOLERANCE);
  }

  [Test]
  public void ResolveLengthScaleToMillimeters_MillimetrePrefixedUnit_Resolves1()
  {
    string path = WriteTempStepFile(
      "#6=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);",
      "#27=IFCUNITASSIGNMENT((#6));",
      "#34=IFCPROJECT('3P8NxVEjL0w8oZ8J8rWhAH',$,'Project',$,$,$,$,$,#27);"
    );
    try
    {
      using var doc = new StepDocument(path);
      var graph = StepGraph.Create(doc);

      double scale = IfcUnitResolver.ResolveLengthScaleToMillimeters(graph);

      scale.Should().BeApproximately(1, TOLERANCE);
    }
    finally
    {
      File.Delete(path);
    }
  }

  [Test]
  public void ResolveLengthScaleToMillimeters_ConversionBasedFootUnit_ResolvesToConversionFactor()
  {
    // Mirrors the real Tekla file's own (unused, but present) FOOT unit: 1 FOOT = 304.8 [of the base SI
    // millimetre unit] - a conversion-based unit expressed relative to another unit, requiring one level
    // of recursion to resolve.
    string path = WriteTempStepFile(
      "#6=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);",
      "#7=IFCMEASUREWITHUNIT(IFCRATIOMEASURE(304.8),#6);",
      "#8=IFCDIMENSIONALEXPONENTS(1,0,0,0,0,0,0);",
      "#9=IFCCONVERSIONBASEDUNIT(#8,.LENGTHUNIT.,'FOOT',#7);",
      "#27=IFCUNITASSIGNMENT((#9));",
      "#34=IFCPROJECT('3P8NxVEjL0w8oZ8J8rWhAH',$,'Project',$,$,$,$,$,#27);"
    );
    try
    {
      using var doc = new StepDocument(path);
      var graph = StepGraph.Create(doc);

      double scale = IfcUnitResolver.ResolveLengthScaleToMillimeters(graph);

      scale.Should().BeApproximately(304.8, TOLERANCE);
    }
    finally
    {
      File.Delete(path);
    }
  }

  [Test]
  public void ResolveLengthScaleToMillimeters_NoProjectInFile_DefaultsTo1()
  {
    string path = WriteTempStepFile("#1=IFCCARTESIANPOINT((0.,0.,0.));");
    try
    {
      using var doc = new StepDocument(path);
      var graph = StepGraph.Create(doc);

      double scale = IfcUnitResolver.ResolveLengthScaleToMillimeters(graph);

      scale.Should().BeApproximately(1, TOLERANCE);
    }
    finally
    {
      File.Delete(path);
    }
  }

  private static string WriteTempStepFile(params string[] dataLines)
  {
    string path = Path.Combine(Path.GetTempPath(), $"ifc-unit-resolver-test-{Guid.NewGuid():N}.ifc");
    var lines = new List<string>
    {
      "ISO-10303-21;",
      "HEADER;",
      "FILE_DESCRIPTION((''),'2;1');",
      "FILE_NAME('','',(''),(''),'','','');",
      "FILE_SCHEMA(('IFC4'));",
      "ENDSEC;",
      "DATA;",
    };
    lines.AddRange(dataLines);
    lines.Add("ENDSEC;");
    lines.Add("END-ISO-10303-21;");
    File.WriteAllLines(path, lines);
    return path;
  }
}
