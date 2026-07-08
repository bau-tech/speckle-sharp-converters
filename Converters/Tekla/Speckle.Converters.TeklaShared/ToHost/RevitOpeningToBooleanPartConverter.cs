using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.TeklaShared.Helpers;
using Speckle.Objects.Data;
using Speckle.Sdk.Common;
using Speckle.Sdk.Common.Exceptions;

namespace Speckle.Converters.TeklaShared.ToHost;

/// <summary>
/// Converts a Revit Opening (arriving as a <see cref="RevitObject"/>, hosted on a Wall/Floor/Column/Beam
/// already converted in Pass 1) into a <see cref="TSM.BooleanPart"/> cut on that host part.
/// </summary>
public class RevitOpeningToBooleanPartConverter
{
  // Cutter "thickness" margin: the operative ContourPlate's profile thickness must exceed the
  // host's actual thickness so Position.Depth=MIDDLE produces a cut that fully penetrates.
  private const double DEFAULT_CUT_PLATE_THICKNESS_MM = 400;

  private readonly TeklaReceiveCache _receiveCache;
  private readonly ITypedConverter<SOG.Point, TG.Point> _pointConverter;
  private readonly IConverterSettingsStore<TeklaConversionSettings> _settingsStore;
  private readonly ILogger<RevitOpeningToBooleanPartConverter> _logger;

  public RevitOpeningToBooleanPartConverter(
    TeklaReceiveCache receiveCache,
    ITypedConverter<SOG.Point, TG.Point> pointConverter,
    IConverterSettingsStore<TeklaConversionSettings> settingsStore,
    ILogger<RevitOpeningToBooleanPartConverter> logger
  )
  {
    _receiveCache = receiveCache;
    _pointConverter = pointConverter;
    _settingsStore = settingsStore;
    _logger = logger;
  }

  public TSM.BooleanPart ConvertAsBooleanCut(RevitObject target)
  {
    string? hostAppId = target.properties.GetOrDefault("parentApplicationId") as string;
    if (hostAppId is null || _receiveCache.Get(hostAppId) is not TSM.Part fatherPart)
    {
      throw new ConversionException($"Opening host '{hostAppId}' has not been received yet.");
    }

    if (target["location"] is not SOG.Polycurve boundary)
    {
      throw new ConversionException("Revit Opening requires a polycurve boundary location.");
    }

    // Every receive of the same source opening previously ran through here unconditionally,
    // inserting a brand-new native BooleanPart with no awareness of ones inserted by earlier receives
    // of the SAME opening - since the host Part's identity IS correctly preserved/reused across
    // resends, re-receiving the same Revit model N times stacked N duplicate cuts onto that one host.
    // Mirror the beam/plate mechanism instead: tag inserted cuts with the origin applicationId
    // (TeklaOriginIdentifier) and replace the previously-tagged cut in place.
    string? originApplicationId = target.applicationId ?? target.id;
    if (originApplicationId is not null && FindExistingBooleanCut(fatherPart, originApplicationId) is { } existingCut)
    {
      bool existingDeleted = existingCut.Delete();
      _logger.LogDebug(
        "Replacing previously-received opening cut identifier={Id} deleted={Deleted}",
        existingCut.Identifier,
        existingDeleted
      );
    }

    double scale = RevitPropertyReader.GetUnitScaleFactor(target.units, _settingsStore.Current.SpeckleUnits);

    TSM.Contour cutterContour = BuildCutterContour(boundary, scale);

    var operativePart = new TSM.ContourPlate
    {
      Contour = cutterContour,
      Class = TSM.BooleanPart.BooleanOperativeClassName, // required sentinel
    };

    double thicknessInModelUnits =
      DEFAULT_CUT_PLATE_THICKNESS_MM
      * RevitPropertyReader.GetUnitScaleFactor(Units.Millimeters, _settingsStore.Current.SpeckleUnits);
    operativePart.Profile.ProfileString = $"PL{thicknessInModelUnits:0}";

    // Position: MIDDLE/MIDDLE so the cutter's extruded thickness straddles the boundary plane,
    // ensuring it passes fully through the host. Set AFTER geometry is assigned.
    operativePart.Position.Depth = TSM.Position.DepthEnum.MIDDLE;
    operativePart.Position.Plane = TSM.Position.PlaneEnum.MIDDLE;
    operativePart.Position.Rotation = TSM.Position.RotationEnum.FRONT;

    operativePart.Insert(); // must have model identity before attaching to BooleanPart

    var booleanPart = new TSM.BooleanPart
    {
      Father = fatherPart,
      OperativePart = operativePart,
      Type = TSM.BooleanPart.BooleanTypeEnum.BOOLEAN_CUT,
    };

    bool inserted = booleanPart.Insert();
    if (inserted)
    {
      operativePart.Delete(); // consumed by the boolean; remove standalone copy
      if (originApplicationId is not null)
      {
        TeklaOriginIdentifier.Set(booleanPart, originApplicationId, _logger);
      }
    }
    else
    {
      // Insert() rejected the boolean outright - the operative never got consumed, so it must be
      // cleaned up here too, otherwise it lingers in the model as an orphaned "BlOpCl" part.
      // Throwing (rather than returning the uninserted part) lets the caller's catch block report
      // this as the failure it is, instead of logging a false "SUCCESS".
      operativePart.Delete();
      throw new ConversionException($"BooleanPart.Insert() failed for opening id={target.id}.");
    }

    return booleanPart;
  }

  private static TSM.BooleanPart? FindExistingBooleanCut(TSM.Part fatherPart, string originApplicationId)
  {
    var booleans = fatherPart.GetBooleans();
    while (booleans.MoveNext())
    {
      if (
        booleans.Current is TSM.BooleanPart bp
        && TeklaOriginIdentifier.TryGet(bp, out string? existingOriginId)
        && existingOriginId == originApplicationId
      )
      {
        return bp;
      }
    }
    return null;
  }

  private TSM.Contour BuildCutterContour(SOG.Polycurve boundary, double scale)
  {
    var contour = new TSM.Contour();
    foreach (var (point, chamferRadius) in RevitPropertyReader.PolycurveToScaledPoints(boundary, scale))
    {
      var chamfer =
        chamferRadius > 0
          ? new TSM.Chamfer(chamferRadius, chamferRadius, TSM.Chamfer.ChamferTypeEnum.CHAMFER_ROUNDING)
          : new TSM.Chamfer();
      contour.AddContourPoint(new TSM.ContourPoint(_pointConverter.Convert(point), chamfer));
    }
    return contour;
  }
}
