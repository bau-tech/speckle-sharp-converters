using FluentAssertions;
using NUnit.Framework;
using Speckle.Converters.IfcShared.Extraction;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Tests;

public class IfcMaterialThicknessExtractorTests
{
  private const double TOLERANCE = 1e-9;
  private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-sample.ifc");

  [Test]
  public void TryGetLayerSetThicknessMm_Wall_ResolvesSingleLayerThickness()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);
    var index = IfcMaterialThicknessExtractor.BuildElementToMaterialIndex(graph);

    // #2467's material association goes through IfcMaterialLayerSetUsage -> IfcMaterialLayerSet with
    // a single 300mm layer, mirroring the real wall's exact structure.
    bool resolved = IfcMaterialThicknessExtractor.TryGetLayerSetThicknessMm(graph, index, 2467, out double thicknessMm);

    resolved.Should().BeTrue();
    thicknessMm.Should().BeApproximately(300, TOLERANCE);
  }

  [Test]
  public void TryGetLayerSetThicknessMm_Floor_ResolvesSingleLayerThickness()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);
    var index = IfcMaterialThicknessExtractor.BuildElementToMaterialIndex(graph);

    bool resolved = IfcMaterialThicknessExtractor.TryGetLayerSetThicknessMm(graph, index, 930, out double thicknessMm);

    resolved.Should().BeTrue();
    thicknessMm.Should().BeApproximately(200, TOLERANCE);
  }

  [Test]
  public void TryGetLayerSetThicknessMm_ColumnWithPlainMaterialNoLayers_ReturnsFalseRatherThanGuessing()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);
    var index = IfcMaterialThicknessExtractor.BuildElementToMaterialIndex(graph);

    // #168's material association resolves directly to a plain IfcMaterial (#81, no layers) -
    // mirrors the real column's actual association exactly (confirmed: no IfcMaterialProfileSet).
    bool resolved = IfcMaterialThicknessExtractor.TryGetLayerSetThicknessMm(graph, index, 168, out _);

    resolved.Should().BeFalse();
  }

  [Test]
  public void TryGetLayerSetThicknessMm_ElementWithNoMaterialAssociationAtAll_ReturnsFalse()
  {
    using var doc = new StepDocument(FixturePath);
    var graph = StepGraph.Create(doc);
    var index = IfcMaterialThicknessExtractor.BuildElementToMaterialIndex(graph);

    // #506 (the rectangular beam) has no IfcRelAssociatesMaterial entry at all in this fixture.
    bool resolved = IfcMaterialThicknessExtractor.TryGetLayerSetThicknessMm(graph, index, 506, out _);

    resolved.Should().BeFalse();
  }
}
