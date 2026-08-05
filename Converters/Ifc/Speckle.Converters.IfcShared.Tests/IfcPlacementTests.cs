using FluentAssertions;
using NUnit.Framework;
using Speckle.Converters.IfcShared.Geometry;

namespace Speckle.Converters.IfcShared.Tests;

/// <summary>
/// Pure-math tests for placement composition - independent of STEP parsing, so the composition logic
/// itself (this feature's flagged highest-risk piece) can be verified against hand-computed values,
/// including a rotated case the real live-tested file never exercised (it had zero rotated elements).
/// </summary>
public class IfcPlacementTests
{
  private const double TOLERANCE = 1e-9;

  private static void AssertVector(IfcVector3 actual, double x, double y, double z)
  {
    actual.X.Should().BeApproximately(x, TOLERANCE);
    actual.Y.Should().BeApproximately(y, TOLERANCE);
    actual.Z.Should().BeApproximately(z, TOLERANCE);
  }

  [Test]
  public void FromAxis2Placement3D_NullAxisAndRefDirection_DefaultsToWorldFrame()
  {
    var placement = IfcPlacement.FromAxis2Placement3D(new IfcVector3(5, 6, 7), null, null);

    AssertVector(placement.Origin, 5, 6, 7);
    AssertVector(placement.XAxis, 1, 0, 0);
    AssertVector(placement.ZAxis, 0, 0, 1);
  }

  [Test]
  public void Compose_RotatedParentWithChildOffset_MatchesHandComputedResult()
  {
    // Parent: Location=(100,200,0), Axis=(0,0,1) [Z unchanged], RefDirection=(0,1,0) [local X -> world Y].
    var parent = IfcPlacement.FromAxis2Placement3D(
      new IfcVector3(100, 200, 0),
      new IfcVector3(0, 0, 1),
      new IfcVector3(0, 1, 0)
    );

    // Hand-computed: Gram-Schmidt gives X=(0,1,0), Y=Z×X=(-1,0,0), Z=(0,0,1).
    AssertVector(parent.XAxis, 0, 1, 0);
    AssertVector(parent.YAxis, -1, 0, 0);
    AssertVector(parent.ZAxis, 0, 0, 1);

    // Child: Location=(10,0,0) relative to parent, identity orientation.
    var child = IfcPlacement.FromAxis2Placement3D(new IfcVector3(10, 0, 0), null, null);

    var absolute = parent.Compose(child);

    // Hand-computed: origin = parent.Origin + parent.X*10 = (100,200,0) + (0,10,0) = (100,210,0).
    AssertVector(absolute.Origin, 100, 210, 0);
    AssertVector(absolute.XAxis, 0, 1, 0);
    AssertVector(absolute.YAxis, -1, 0, 0);
    AssertVector(absolute.ZAxis, 0, 0, 1);
  }

  [Test]
  public void Compose_IdentityChain_IsUnchanged()
  {
    var parent = IfcPlacement.FromAxis2Placement3D(new IfcVector3(50, 0, 0), null, null);
    var child = IfcPlacement.FromAxis2Placement3D(new IfcVector3(0, 25, 0), null, null);

    var absolute = parent.Compose(child);

    AssertVector(absolute.Origin, 50, 25, 0);
    AssertVector(absolute.XAxis, 1, 0, 0);
  }
}
