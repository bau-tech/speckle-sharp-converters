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
  // host's actual thickness so the cut fully penetrates.
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

    // Tekla model coordinates are always millimeters (see PointToHostConverter), regardless of the
    // Tekla Options>Units display setting captured in _settingsStore.Current.SpeckleUnits.
    double scale = RevitPropertyReader.GetUnitScaleFactor(target.units, Units.Millimeters);

    TSM.Contour cutterContour = BuildCutterContour(boundary, scale);

    var operativePart = new TSM.ContourPlate
    {
      Contour = cutterContour,
      Class = TSM.BooleanPart.BooleanOperativeClassName, // required sentinel
    };

    // Tekla parametric plate profiles ("PL400") are in millimeters, so no scaling is needed here -
    // the previous scale to _settingsStore.Current.SpeckleUnits (the Options>Units display setting)
    // produced a wrong profile thickness whenever that setting wasn't millimeters.
    double thicknessInModelUnits = DEFAULT_CUT_PLATE_THICKNESS_MM;
    operativePart.Profile.ProfileString = $"PL{thicknessInModelUnits:0}";

    // Depth=MIDDLE was found (2026-07-10, live Tekla test) to cut only partway through the host -
    // BEHIND (confirmed working manually via the part's own Position dialog) reliably passes fully
    // through a ContourPlate host (floor/foundation slab). A wall host is a Beam/PolyBeam instead (see
    // RevitWallToTeklaBeamConverter, itself Depth=FRONT), and BEHIND lands the cutter on the wrong
    // side of the wall's own material there - confirmed live via the IFC-sourced mirror of this
    // converter (openings appeared outside the wall; see IfcOpeningToBooleanPartConverter for the same
    // fix). Match the host's own Depth convention instead of a single fixed value. Set AFTER geometry
    // is assigned.
    operativePart.Position.Depth =
      fatherPart is TSM.ContourPlate ? TSM.Position.DepthEnum.BEHIND : TSM.Position.DepthEnum.FRONT;
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
