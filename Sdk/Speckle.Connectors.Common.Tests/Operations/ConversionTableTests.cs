using FluentAssertions;
using NUnit.Framework;
using Speckle.Connectors.Common.Operations;
using Speckle.Testing;

namespace Speckle.Connectors.Common.Tests.Operations;

public class ConversionTableTests : MoqTest
{
  [Test]
  public void ToWire_ThenTryParse_RoundTrips()
  {
    var table = new ConversionTable { SourceApplication = "Revit" };
    table.Profiles.Add(
      new ConversionTableProfileEntry
      {
        Category = "Structural Framing",
        Family = "W-Wide Flange",
        Type = "W12x26",
        WidthMm = 165.1,
        HeightMm = 310.6,
      }
    );
    table.Materials.Add(new ConversionTableMaterialEntry { Name = "Steel, ASTM A992" });

    var wire = table.ToWire();
    bool parsed = ConversionTable.TryParse(wire, out var result);

    parsed.Should().BeTrue();
    result.SourceApplication.Should().Be("Revit");
    result.Profiles.Should().ContainSingle();
    result.Profiles[0].Category.Should().Be("Structural Framing");
    result.Profiles[0].Family.Should().Be("W-Wide Flange");
    result.Profiles[0].Type.Should().Be("W12x26");
    result.Profiles[0].WidthMm.Should().Be(165.1);
    result.Profiles[0].HeightMm.Should().Be(310.6);
    result.Materials.Should().ContainSingle();
    result.Materials[0].Name.Should().Be("Steel, ASTM A992");
  }

  [Test]
  public void ToWire_ThenTryParse_MissingOptionalDimensions_StillParses()
  {
    var table = new ConversionTable { SourceApplication = "Tekla" };
    table.Profiles.Add(
      new ConversionTableProfileEntry
      {
        Category = "Beam",
        Family = "HEA",
        Type = "HEA200",
      }
    );

    bool parsed = ConversionTable.TryParse(table.ToWire(), out var result);

    parsed.Should().BeTrue();
    result.Profiles.Should().ContainSingle();
    result.Profiles[0].WidthMm.Should().BeNull();
    result.Profiles[0].HeightMm.Should().BeNull();
  }

  [Test]
  public void TryParse_EmptyTable_ReturnsFalse()
  {
    var table = new ConversionTable { SourceApplication = "Revit" };

    bool parsed = ConversionTable.TryParse(table.ToWire(), out var result);

    parsed.Should().BeFalse();
    result.Profiles.Should().BeEmpty();
    result.Materials.Should().BeEmpty();
  }

  [Test]
  [TestCase(null)]
  [TestCase("not a dictionary")]
  [TestCase(42)]
  public void TryParse_NonDictionaryInput_ReturnsFalse(object? wireValue)
  {
    bool parsed = ConversionTable.TryParse(wireValue, out var result);

    parsed.Should().BeFalse();
    result.Profiles.Should().BeEmpty();
    result.Materials.Should().BeEmpty();
  }

  [Test]
  public void TryParse_ProfileEntryMissingType_IsSkipped()
  {
    var wire = new Dictionary<string, object?>
    {
      ["sourceApplication"] = "Revit",
      ["profiles"] = new List<object>
      {
        new Dictionary<string, object?> { ["category"] = "Beam", ["family"] = "W-Wide Flange" },
      },
      ["materials"] = new List<object>(),
    };

    bool parsed = ConversionTable.TryParse(wire, out var result);

    parsed.Should().BeFalse();
    result.Profiles.Should().BeEmpty();
  }

  [Test]
  [TestCase(typeof(long))]
  [TestCase(typeof(double))]
  public void TryParse_DimensionsBoxedAsLongOrDouble_AreParsed(Type boxedType)
  {
    object widthValue = boxedType == typeof(long) ? 200L : 200.0;
    object heightValue = boxedType == typeof(long) ? 400L : 400.0;
    var wire = new Dictionary<string, object?>
    {
      ["sourceApplication"] = "Revit",
      ["profiles"] = new List<object>
      {
        new Dictionary<string, object?>
        {
          ["category"] = "Beam",
          ["family"] = "W-Wide Flange",
          ["type"] = "W12x26",
          ["widthMm"] = widthValue,
          ["heightMm"] = heightValue,
        },
      },
      ["materials"] = new List<object>(),
    };

    bool parsed = ConversionTable.TryParse(wire, out var result);

    parsed.Should().BeTrue();
    result.Profiles.Should().ContainSingle();
    result.Profiles[0].WidthMm.Should().Be(200.0);
    result.Profiles[0].HeightMm.Should().Be(400.0);
  }

  [Test]
  public void TryParse_MaterialEntryMissingName_IsSkipped()
  {
    var wire = new Dictionary<string, object?>
    {
      ["sourceApplication"] = "Tekla",
      ["profiles"] = new List<object>(),
      ["materials"] = new List<object> { new Dictionary<string, object?> { ["notName"] = "S235JR" } },
    };

    bool parsed = ConversionTable.TryParse(wire, out var result);

    parsed.Should().BeFalse();
    result.Materials.Should().BeEmpty();
  }
}
