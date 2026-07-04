using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Speckle.Converters.TeklaShared.Helpers.ProfileMapping;
using Speckle.Testing;

namespace Speckle.Converters.TeklaShared.Tests;

public class TeklaCatalogValidatorTests : MoqTest
{
  // Numeric parametric profiles are always valid via the regex fast path in IsValidProfile,
  // short-circuiting before any Tekla catalog API call - safe to test without a live Tekla session.
  [Test]
  [TestCase("500*300")]
  [TestCase("500x300")]
  [TestCase("500X300")]
  [TestCase("D400")]
  [TestCase("d400")]
  [TestCase("PL250")]
  [TestCase("pl250")]
  [TestCase("300")]
  [TestCase("300.5*150.25")]
  public void IsValidProfile_NumericParametricDesignation_ReturnsTrueWithoutCatalogAccess(string profile)
  {
    var logger = Create<ILogger<TeklaCatalogValidator>>();
    var sut = new TeklaCatalogValidator(logger.Object);

    sut.IsValidProfile(profile).Should().BeTrue();
  }

  [Test]
  public void ValidateFirstOrFallback_AllCandidatesNullOrBlank_ReturnsDefaultWithNoAttemptWarning()
  {
    var logger = Create<ILogger<TeklaCatalogValidator>>();
    var sut = new TeklaCatalogValidator(logger.Object);

    var (value, warning) = sut.ValidateFirstOrFallback([null, "", "  "], "HEA200", isProfile: true);

    value.Should().Be("HEA200");
    warning.Should().Contain("No profile was captured");
  }

  [Test]
  public void ValidateFirstOrFallback_FirstNumericParametricCandidateWins()
  {
    var logger = Create<ILogger<TeklaCatalogValidator>>();
    var sut = new TeklaCatalogValidator(logger.Object);

    var (value, warning) = sut.ValidateFirstOrFallback(["500*300", "D400"], "HEA200", isProfile: true);

    value.Should().Be("500*300");
    warning.Should().BeNull();
  }
}
