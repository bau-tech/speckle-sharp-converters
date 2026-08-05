namespace Speckle.Converters.IfcShared.Geometry;

/// <summary>
/// An orthonormal 3D reference frame (origin + right-handed X/Y/Z basis), representing an absolute
/// (world-space) IFC placement produced by composing an <c>IfcLocalPlacement</c> chain.
/// </summary>
/// <remarks>
/// This is the piece flagged as this feature's biggest correctness risk: an <c>IfcLocalPlacement</c>'s
/// <c>RelativePlacement</c> is defined relative to its <c>PlacementRelTo</c> parent, recursively up to
/// the site - getting that composition wrong produces plausible-but-wrong geometry that's easy to miss
/// in casual QA. This type and <see cref="Compose"/> are the one place that math happens, so it can be
/// unit-tested in isolation with hand-computed expected values.
/// </remarks>
public sealed class IfcPlacement
{
  public IfcVector3 Origin { get; }
  public IfcVector3 XAxis { get; }
  public IfcVector3 YAxis { get; }
  public IfcVector3 ZAxis { get; }

  public static readonly IfcPlacement Identity = new(
    IfcVector3.Zero,
    IfcVector3.UnitX,
    IfcVector3.UnitZ.Cross(IfcVector3.UnitX),
    IfcVector3.UnitZ
  );

  private IfcPlacement(IfcVector3 origin, IfcVector3 xAxis, IfcVector3 yAxis, IfcVector3 zAxis)
  {
    Origin = origin;
    XAxis = xAxis;
    YAxis = yAxis;
    ZAxis = zAxis;
  }

  /// <summary>
  /// Builds a placement from an <c>IfcAxis2Placement3D</c>'s <c>Location</c>/<c>Axis</c>/
  /// <c>RefDirection</c> - <c>Axis</c>/<c>RefDirection</c> default to Z-up/X-forward when null
  /// (IFC schema default, per <c>IfcAxis2Placement3D</c>), otherwise built via Gram-Schmidt
  /// orthogonalization exactly as the IFC spec defines: Z is the (normalized) <c>Axis</c>; X is
  /// <c>RefDirection</c> with its component along Z removed, then normalized; Y completes a
  /// right-handed frame.
  /// </summary>
  public static IfcPlacement FromAxis2Placement3D(IfcVector3 location, IfcVector3? axis, IfcVector3? refDirection)
  {
    IfcVector3 z = (axis ?? IfcVector3.UnitZ).Normalized();
    IfcVector3 refDir = refDirection ?? IfcVector3.UnitX;

    // Gram-Schmidt: remove the component of refDir along z, then normalize.
    IfcVector3 xUnnormalized = refDir - z * z.Dot(refDir);
    IfcVector3 x = xUnnormalized.Length < 1e-9 ? FallbackXAxis(z) : xUnnormalized.Normalized();

    IfcVector3 y = z.Cross(x);
    return new IfcPlacement(location, x, y, z);
  }

  /// <summary>
  /// Builds a placement from an <c>IfcAxis2Placement2D</c>'s <c>Location</c>/<c>RefDirection</c> (a
  /// profile definition's own local 2D placement, e.g. <c>IfcRectangleProfileDef.Position</c> - see
  /// <see cref="Extraction.IfcProfileExtractor"/>'s remarks) - treated as lying flat in its parent
  /// frame's local XY plane: <c>ZAxis</c> is always +Z (2D has no separate axis attribute), <c>XAxis</c>
  /// is <c>RefDirection</c> (default +X when null, per the IFC schema default), <c>YAxis</c> completes
  /// a right-handed frame. <paramref name="location"/>'s Z is expected to already be 0 (a 2D point).
  /// </summary>
  public static IfcPlacement FromAxis2Placement2D(IfcVector3 location, IfcVector3? refDirection)
  {
    IfcVector3 z = IfcVector3.UnitZ;
    IfcVector3 x = (refDirection ?? IfcVector3.UnitX).Normalized();
    IfcVector3 y = z.Cross(x);
    return new IfcPlacement(location, x, y, z);
  }

  /// <summary>
  /// Composes <paramref name="local"/> (a placement expressed relative to this one - i.e. this is the
  /// <c>PlacementRelTo</c> parent) into world space: <paramref name="local"/>'s origin and axes are
  /// transformed by this placement's own frame, exactly mirroring how nested <c>IfcLocalPlacement</c>s
  /// compose.
  /// </summary>
  public IfcPlacement Compose(IfcPlacement local) =>
    new(
      TransformPoint(local.Origin),
      TransformDirection(local.XAxis),
      TransformDirection(local.YAxis),
      TransformDirection(local.ZAxis)
    );

  /// <summary>Transforms a point given in this placement's local coordinates into world coordinates.</summary>
  public IfcVector3 TransformPoint(IfcVector3 local) => Origin + TransformDirection(local);

  /// <summary>Transforms a direction (no translation) given in this placement's local coordinates into world coordinates.</summary>
  public IfcVector3 TransformDirection(IfcVector3 local) => XAxis * local.X + YAxis * local.Y + ZAxis * local.Z;

  // RefDirection is degenerate (parallel to Axis, or missing) - the IFC default X (1,0,0) can't be
  // used directly since it may itself be parallel to Z. Pick any vector not parallel to Z.
  private static IfcVector3 FallbackXAxis(IfcVector3 z)
  {
    IfcVector3 candidate = Math.Abs(z.Dot(IfcVector3.UnitX)) < 0.9 ? IfcVector3.UnitX : new IfcVector3(0, 1, 0);
    return (candidate - z * z.Dot(candidate)).Normalized();
  }
}
