namespace Speckle.Converters.IfcShared.Geometry;

/// <summary>
/// A plain 3D vector/point, used only for composing IFC placement chains
/// (<see cref="IfcPlacement"/>) - deliberately independent of any host CAD geometry type, since this
/// project has no host-application dependency.
/// </summary>
public readonly struct IfcVector3(double x, double y, double z)
{
  public double X { get; } = x;
  public double Y { get; } = y;
  public double Z { get; } = z;

  public static readonly IfcVector3 Zero = new(0, 0, 0);
  public static readonly IfcVector3 UnitX = new(1, 0, 0);
  public static readonly IfcVector3 UnitZ = new(0, 0, 1);

  public static IfcVector3 operator +(IfcVector3 a, IfcVector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

  public static IfcVector3 operator -(IfcVector3 a, IfcVector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

  public static IfcVector3 operator *(IfcVector3 a, double scalar) => new(a.X * scalar, a.Y * scalar, a.Z * scalar);

  public double Dot(IfcVector3 other) => X * other.X + Y * other.Y + Z * other.Z;

  public IfcVector3 Cross(IfcVector3 other) =>
    new(Y * other.Z - Z * other.Y, Z * other.X - X * other.Z, X * other.Y - Y * other.X);

  public double Length => Math.Sqrt(Dot(this));

  /// <summary>Returns a unit-length vector, or <see cref="UnitZ"/> if this vector is (near) zero.</summary>
  public IfcVector3 Normalized()
  {
    double length = Length;
    return length < 1e-9 ? UnitZ : this * (1.0 / length);
  }
}
