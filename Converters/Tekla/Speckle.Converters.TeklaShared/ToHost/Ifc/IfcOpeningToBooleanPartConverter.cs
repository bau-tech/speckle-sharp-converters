using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.TeklaShared.Helpers;
using Speckle.Objects.Data;
using Speckle.Sdk.Common.Exceptions;

namespace Speckle.Converters.TeklaShared.ToHost.Ifc;

/// <summary>
/// Converts an IFC-enriched opening <see cref="DataObject"/> (synthesized directly by the enricher
/// - see <c>RevitNativeSchemaEnricher.TryBuildOpeningDataObject</c>'s remarks, since
/// <c>IfcOpeningElement</c> is never uploaded as its own Speckle object) into a
/// <see cref="TSM.BooleanPart"/> cut on its already-converted host part. Mirrors
/// <see cref="RevitOpeningToBooleanPartConverter"/>, reading the enricher's boundary as a plain
/// <see cref="SOG.Polyline"/> (always straight-cornered) instead of a <see cref="SOG.Polycurve"/>.
/// </summary>
public class IfcOpeningToBooleanPartConverter
{
  private const double DEFAULT_CUT_PLATE_THICKNESS_MM = 400;

  // The IFC opening boundary plane is not guaranteed to sit at the wall's mid-thickness - it commonly
  // lands close to one face (e.g. a window reveal plane), up to a full wall-thickness away from the
  // opposite face. A Depth=MIDDLE cutter must reach that far face from wherever the boundary actually
  // is, so its half-thickness needs to exceed the thickest wall likely to be cut, not just the typical
  // one. 1000mm covers walls up to ~1m thick (way beyond DEFAULT_WALL_THICKNESS_MM=200/observed
  // 240-300mm) with headroom.
  private const double WALL_CUT_PLATE_THICKNESS_MM = 1000;

  private readonly TeklaReceiveCache _receiveCache;
  private readonly ILogger<IfcOpeningToBooleanPartConverter> _logger;

  public IfcOpeningToBooleanPartConverter(
    TeklaReceiveCache receiveCache,
    ILogger<IfcOpeningToBooleanPartConverter> logger
  )
  {
    _receiveCache = receiveCache;
    _logger = logger;
  }

  public TSM.BooleanPart ConvertAsBooleanCut(DataObject target)
  {
    string? hostAppId = target.properties.GetOrDefault("parentApplicationId") as string;
    if (hostAppId is null || _receiveCache.Get(hostAppId) is not TSM.Part fatherPart)
    {
      throw new ConversionException($"IFC opening host '{hostAppId}' has not been received yet.");
    }

    if (target["location"] is not SOG.Polyline boundary || boundary.value.Count < 9)
    {
      throw new ConversionException("IFC-sourced opening requires a polyline boundary location.");
    }

    // Mirrors RevitOpeningToBooleanPartConverter: tag inserted cuts with the origin applicationId so
    // a re-receive of the same source opening replaces its previous cut in place instead of stacking
    // a new one onto the (identity-preserved) host part.
    string? originApplicationId = target.applicationId ?? target.id;
    if (originApplicationId is not null && FindExistingBooleanCut(fatherPart, originApplicationId) is { } existingCut)
    {
      bool existingDeleted = existingCut.Delete();
      _logger.LogDebug(
        "Replacing previously-received IFC opening cut identifier={Id} deleted={Deleted}",
        existingCut.Identifier,
        existingDeleted
      );
    }

    TSM.Contour cutterContour = BuildCutterContour(boundary);

    bool isWallHost = fatherPart is not TSM.ContourPlate;
    var operativePart = new TSM.ContourPlate
    {
      Contour = cutterContour,
      Class = TSM.BooleanPart.BooleanOperativeClassName, // required sentinel
    };
    double cutPlateThicknessMm = isWallHost ? WALL_CUT_PLATE_THICKNESS_MM : DEFAULT_CUT_PLATE_THICKNESS_MM;
    operativePart.Profile.ProfileString = FormattableString.Invariant($"PL{cutPlateThicknessMm:0}");
    // Depth=BEHIND reliably passes fully through a ContourPlate host (floor/foundation slab; mirrors
    // RevitOpeningToBooleanPartConverter's own finding - Depth=MIDDLE only cuts partway through). A
    // wall host is a Beam/PolyBeam instead (see IfcWallToTeklaBeamConverter). Matching the wall's own
    // Depth=FRONT convention there guessed wrong in some cases - whichever side of the wall's
    // thickness the IFC boundary polyline happens to sit on isn't guaranteed to be the wall's own
    // FRONT side, so the cutter landed off to one side, missing part of the wall. Depth=MIDDLE avoids
    // the direction guess entirely by centering the cutter symmetrically on the boundary polyline
    // itself (the wall's longitudinal axis) - but that only fully spans the wall if the cutter's
    // half-thickness reaches the far face, and the boundary plane is not guaranteed to sit at the
    // wall's mid-thickness (it can be up to a full wall-thickness away from one face) - hence the
    // oversized WALL_CUT_PLATE_THICKNESS_MM for this branch. ContourPlate hosts keep BEHIND.
    operativePart.Position.Depth = isWallHost ? TSM.Position.DepthEnum.MIDDLE : TSM.Position.DepthEnum.BEHIND;
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
      operativePart.Delete();
      throw new ConversionException($"BooleanPart.Insert() failed for IFC opening id={target.id}.");
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

  private static TSM.Contour BuildCutterContour(SOG.Polyline boundary)
  {
    var contour = new TSM.Contour();
    for (int i = 0; i < boundary.value.Count; i += 3)
    {
      contour.AddContourPoint(
        new TSM.ContourPoint(
          new TG.Point(boundary.value[i], boundary.value[i + 1], boundary.value[i + 2]),
          new TSM.Chamfer()
        )
      );
    }
    return contour;
  }
}
