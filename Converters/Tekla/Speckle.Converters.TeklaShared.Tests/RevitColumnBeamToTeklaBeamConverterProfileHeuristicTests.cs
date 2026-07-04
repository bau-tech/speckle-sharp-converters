using FluentAssertions;
using NUnit.Framework;
using Speckle.Converters.TeklaShared.ToHost;
using Speckle.Objects.Data;
using Speckle.Testing;

namespace Speckle.Converters.TeklaShared.Tests;

public class RevitColumnBeamToTeklaBeamConverterProfileHeuristicTests : MoqTest
{
  private static RevitObject MakeRevitObject(
    string type = "",
    string name = "",
    Dictionary<string, object?>? instanceParams = null,
    Dictionary<string, object?>? typeParams = null
  ) =>
    new()
    {
      type = type,
      name = name,
      family = "TestFamily",
      category = "Structural Framing",
      level = null,
      location = null,
      elements = [],
      units = "mm",
      displayValue = [],
      properties = new Dictionary<string, object?>
      {
        ["Parameters"] = new Dictionary<string, object>
        {
          ["Instance Parameters"] = new Dictionary<string, object> { ["Group"] = instanceParams ?? new() },
          ["Type Parameters"] = new Dictionary<string, object> { ["Group"] = typeParams ?? new() },
        },
      },
    };

  private static Dictionary<string, object?> LengthParam(
    string internalDefinitionName,
    double valueMm
  ) =>
    new()
    {
      ["internalDefinitionName"] = internalDefinitionName,
      ["value"] = valueMm,
      ["unitsTypeId"] = "autodesk.unit.unit:millimeters-1.0.1",
    };

  [Test]
  [TestCase("HEA200", true)]
  [TestCase("hea200", true)]
  [TestCase("IPE300", true)]
  [TestCase("UC 305x305x137", true)]
  [TestCase("RHS100x50x5", true)]
  [TestCase("L50x50x5", true)]
  [TestCase("Concrete-Rectangular-Column", false)]
  [TestCase("", false)]
  public void LooksLikeProfileDesignation_MatchesKnownPrefixes(string typeName, bool expected)
  {
    RevitColumnBeamToTeklaBeamConverter.LooksLikeProfileDesignation(typeName).Should().Be(expected);
  }

  [Test]
  public void TryMapProfileHeuristic_TypeNameLooksLikeDesignation_ReturnsVerbatim()
  {
    var target = MakeRevitObject(type: "HEA300");

    RevitColumnBeamToTeklaBeamConverter.TryMapProfileHeuristic(target).Should().Be("HEA300");
  }

  [Test]
  public void TryMapProfileHeuristic_NoDesignationButRoundDiameterParamPresent_ReturnsRoundProfile()
  {
    var target = MakeRevitObject(
      type: "Concrete-Round-Column",
      typeParams: new Dictionary<string, object?> { ["d"] = LengthParam("d", 400) }
    );

    RevitColumnBeamToTeklaBeamConverter.TryMapProfileHeuristic(target).Should().Be("D400");
  }

  [Test]
  public void TryMapProfileHeuristic_NoDesignationButRectangularParamsPresent_ReturnsRectangularProfile()
  {
    var target = MakeRevitObject(
      type: "Concrete-Rectangular-Column",
      typeParams: new Dictionary<string, object?>
      {
        ["b"] = LengthParam("b", 300),
        ["h"] = LengthParam("h", 500),
      }
    );

    RevitColumnBeamToTeklaBeamConverter.TryMapProfileHeuristic(target).Should().Be("500*300");
  }

  [Test]
  public void TryMapProfileHeuristic_NoUsableSource_ReturnsNull()
  {
    var target = MakeRevitObject(type: "SomeUnrecognizedFamily");

    RevitColumnBeamToTeklaBeamConverter.TryMapProfileHeuristic(target).Should().BeNull();
  }

  [Test]
  public void TryGetRoundProfileMm_PrefersInstanceParamOverMissingTypeParam()
  {
    var target = MakeRevitObject(
      type: "Concrete-Round-Beam",
      instanceParams: new Dictionary<string, object?> { ["Diameter"] = LengthParam("Diameter", 250) }
    );

    RevitColumnBeamToTeklaBeamConverter.TryGetRoundProfileMm(target, out double diameterMm).Should().BeTrue();
    diameterMm.Should().Be(250);
  }

  [Test]
  public void TryGetRectangularProfileMm_MissingHeight_ReturnsFalse()
  {
    var target = MakeRevitObject(
      type: "Concrete-Rectangular-Beam",
      typeParams: new Dictionary<string, object?> { ["b"] = LengthParam("b", 300) }
    );

    RevitColumnBeamToTeklaBeamConverter.TryGetRectangularProfileMm(target, out _, out _).Should().BeFalse();
  }
}
