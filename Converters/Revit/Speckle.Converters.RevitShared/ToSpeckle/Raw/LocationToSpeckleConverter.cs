using Speckle.Converters.Common.Objects;
using Speckle.Objects;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.RevitShared.ToSpeckle;

public class LocationToSpeckleConverter : ITypedConverter<DB.Location, Base>
{
  private readonly ITypedConverter<DB.Curve, ICurve> _curveConverter;
  private readonly ITypedConverter<DB.XYZ, SOG.Point> _xyzConverter;

  public LocationToSpeckleConverter(
    ITypedConverter<DB.Curve, ICurve> curveConverter,
    ITypedConverter<DB.XYZ, SOG.Point> xyzConverter
  )
  {
    _curveConverter = curveConverter;
    _xyzConverter = xyzConverter;
  }

  public Base Convert(DB.Location target)
  {
    return target switch
    {
      DB.LocationCurve curve => (_curveConverter.Convert(curve.Curve) as Base)!, // POC: ICurve and Base are not related but we know they must be, had to soft cast and then !.
      DB.LocationPoint point => ConvertLocationPoint(point),
      _ => throw new ValidationException($"Unexpected location type {target.GetType()}"),
    };
  }

  /// <summary>
  /// Converts a LocationPoint, capturing its plan rotation (e.g. the placement rotation of a structural
  /// column about its vertical axis) as a "rotation" dynamic property on the resulting point - this is not
  /// part of the point's coordinates and would otherwise be lost on round-trip.
  /// </summary>
  private SOG.Point ConvertLocationPoint(DB.LocationPoint point)
  {
    SOG.Point result = _xyzConverter.Convert(point.Point);
    if (point.Rotation != 0)
    {
      result["rotation"] = point.Rotation;
    }

    return result;
  }
}
