using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.TeklaShared.Helpers;
using Speckle.Converters.TeklaShared.Helpers.ProfileMapping;
using Speckle.Objects.Data;
using Speckle.Sdk.Common.Exceptions;

namespace Speckle.Converters.TeklaShared.ToHost.Ifc;

/// <summary>
/// Converts an IFC-enriched <see cref="DataObject"/> (builtInCategory <c>OST_StructuralColumns</c>
/// or <c>OST_StructuralFraming</c> - set by the IFC native-reconstruction enricher shared with the
/// Revit connector, see <c>RevitNativeSchemaEnricher</c>) into a Tekla part. Much simpler than the
/// equivalent <see cref="RevitColumnBeamToTeklaBeamConverter"/>: the enricher already resolved a
/// clean, Tekla-format profile string ("D450"/"800*400") wherever a usable IFC profile existed, and
/// world coordinates are already in millimeters (a plain <see cref="DataObject"/> has no per-source
/// <c>units</c> field to scale from, unlike <see cref="RevitObject"/>) - so no unit scaling, no
/// name/parameter-heuristic profile parsing.
/// </summary>
public class IfcColumnBeamToTeklaBeamConverter : ITypedConverter<DataObject, TSM.Part>
{
  private const string DEFAULT_PROFILE = "HEA200";
  private const string DEFAULT_STEEL_MATERIAL = "S235JR";
  private const string DEFAULT_CONCRETE_MATERIAL = "Concrete_Undefined";

  private readonly ITypedConverter<SOG.Line, TG.LineSegment> _lineConverter;
  private readonly ITypedConverter<SOG.Point, TG.Point> _pointConverter;
  private readonly IfcProfileMappingProvider _mappingProvider;
  private readonly TeklaCatalogValidator _catalogValidator;
  private readonly ConversionWarningCollector _warnings;
  private readonly TeklaExistingBeamIndex _existingBeamIndex;
  private readonly ILogger<IfcColumnBeamToTeklaBeamConverter> _logger;

  public IfcColumnBeamToTeklaBeamConverter(
    ITypedConverter<SOG.Line, TG.LineSegment> lineConverter,
    ITypedConverter<SOG.Point, TG.Point> pointConverter,
    IfcProfileMappingProvider mappingProvider,
    TeklaCatalogValidator catalogValidator,
    ConversionWarningCollector warnings,
    TeklaExistingBeamIndex existingBeamIndex,
    ILogger<IfcColumnBeamToTeklaBeamConverter> logger
  )
  {
    _lineConverter = lineConverter;
    _pointConverter = pointConverter;
    _mappingProvider = mappingProvider;
    _catalogValidator = catalogValidator;
    _warnings = warnings;
    _existingBeamIndex = existingBeamIndex;
    _logger = logger;
  }

  public TSM.Part Convert(DataObject target)
  {
    string? builtInCategory = target["builtInCategory"] as string;
    bool isColumn = builtInCategory == "OST_StructuralColumns";

    TSM.Part part;
    bool isUpdate = false;
    switch (target["location"])
    {
      case SOG.Line line:
      {
        var seg = _lineConverter.Convert(line);
        if (_existingBeamIndex.TryFindExisting(target.applicationId ?? target.id, out var existingBeam))
        {
          existingBeam!.StartPoint = seg.Point1;
          existingBeam.EndPoint = seg.Point2;
          part = existingBeam;
          isUpdate = true;
        }
        else
        {
          part = CreateStraightBeam(target, seg.Point1, seg.Point2);
        }
        break;
      }
      case SOG.Arc arc:
        part = RevitColumnBeamToTeklaBeamConverter.CreateArcPolyBeam(
          _pointConverter.Convert(arc.startPoint),
          _pointConverter.Convert(arc.midPoint),
          _pointConverter.Convert(arc.endPoint)
        );
        break;
      // Point-placed columns/piles: no captured height/bbox data (unlike a Revit column, an IFC
      // pile/column point has no length parameter or displayValue mesh available here) - a fixed
      // default segment upward from the anchor point, flagged so the user knows it's approximate.
      case SOG.Point point:
      {
        var basePt = _pointConverter.Convert(point);
        var topPt = new TG.Point(basePt.X, basePt.Y, basePt.Z + DEFAULT_POINT_PLACED_LENGTH_MM);
        _warnings.Add(
          target.id,
          $"{(isColumn ? "Column" : "Pile")} has no captured axis geometry; used a default "
            + $"{DEFAULT_POINT_PLACED_LENGTH_MM}mm vertical segment from its placement point."
        );

        if (_existingBeamIndex.TryFindExisting(target.applicationId ?? target.id, out var existingColumn))
        {
          existingColumn!.StartPoint = basePt;
          existingColumn.EndPoint = topPt;
          part = existingColumn;
          isUpdate = true;
        }
        else
        {
          part = CreateStraightBeam(target, basePt, topPt);
        }
        break;
      }
      default:
        throw new ConversionException(
          $"IFC-sourced element applicationId={target.applicationId} has an unsupported location of type "
            + $"'{target["location"]?.GetType().Name ?? "null"}' - Tekla Beam conversion needs a line/arc/point location."
        );
    }

    string? ifcTypeName = target["ifcTypeName"] as string;
    var (profile, profileWarning) = ResolveProfile(target, ifcTypeName);
    part.Profile.ProfileString = profile;
    if (profileWarning != null)
    {
      _warnings.Add(target.id, profileWarning);
    }

    var (material, materialWarning) = ResolveMaterial(target);
    part.Material.MaterialString = material;
    if (materialWarning != null)
    {
      _warnings.Add(target.id, materialWarning);
    }

    part.Class = isColumn ? TeklaStandardClasses.ForColumn(material) : TeklaStandardClasses.ForBeam(material);
    part.Position.Plane = TSM.Position.PlaneEnum.MIDDLE;
    part.Position.Rotation = TSM.Position.RotationEnum.TOP;
    // Columns: the enricher's axis (whether a real extrusion-derived line or the single-point
    // fallback) always runs through the profile's own centroid (see RevitNativeSchemaEnricher.
    // TryEnrichColumn), so MIDDLE is correct regardless of location shape - unlike beams, whose
    // location line is the top-of-section reference (BEHIND, matching Tekla's own beam defaults).
    part.Position.Depth = isColumn ? TSM.Position.DepthEnum.MIDDLE : TSM.Position.DepthEnum.BEHIND;
    // Real plan/cross-section rotation, computed geometrically by the enricher (see
    // RevitNativeSchemaEnricher.TryEnrichColumn/TryEnrichBeam's own remarks) since neither a column
    // nor a beam has a captured Revit "rotation"/"STRUCTURAL_BEND_DIR_ANGLE" parameter available for
    // an IFC-sourced element, only geometry - NOT yet live-verified against Tekla's own rotation
    // direction convention (RevitColumnBeamToTeklaBeamConverter's OWN Revit-parameter path needed a
    // sign flip for exactly this reason - "Revit and Tekla rotate in opposite directions" - so this
    // may need the same fix once confirmed against a real rotated element).
    if (target["rotationDegrees"] is double rotationDegrees)
    {
      part.Position.RotationOffset = rotationDegrees;
    }
    part.Name =
      target.name.Length > 0
        ? IfcNameCleaner.StripTrailingTag(target.name)
        : (ifcTypeName ?? (isColumn ? "Column" : "Beam"));

    string? originApplicationId = target.applicationId ?? target.id;
    if (isUpdate)
    {
      part.Modify();
    }
    else
    {
      part.Insert();
    }
    if (originApplicationId is not null)
    {
      TeklaOriginIdentifier.Set(part, originApplicationId, _logger);
    }
    return part;
  }

  private const double DEFAULT_POINT_PLACED_LENGTH_MM = 3000;

  private static TSM.Beam CreateStraightBeam(DataObject target, TG.Point start, TG.Point end)
  {
    double dx = end.X - start.X;
    double dy = end.Y - start.Y;
    double dz = end.Z - start.Z;
    if (dx * dx + dy * dy + dz * dz < 1e-6)
    {
      throw new ConversionException(
        $"IFC-sourced element applicationId={target.applicationId} location resolves to a degenerate "
          + "(zero-length) segment."
      );
    }
    return new TSM.Beam(start, end);
  }

  /// <summary>
  /// The enricher's own <c>profile</c> string wins unconditionally when present (it's already a
  /// clean "D{d}"/"{h}*{w}" Tekla-format string, resolved directly from the IFC extrusion profile -
  /// no heuristic needed). Falls back to a user-maintained ifcTypeName -> profile mapping table for
  /// elements the enricher couldn't resolve a profile for at all (piles, non-standard sections) -
  /// mirrors <c>RevitColumnBeamToTeklaBeamConverter.ResolveProfile</c>'s "candidate list, first
  /// valid one wins" pattern.
  /// </summary>
  private (string Value, string? Warning) ResolveProfile(DataObject target, string? ifcTypeName)
  {
    var candidates = new List<string?> { target["profile"] as string };
    if (ifcTypeName is not null && _mappingProvider.TryGetProfile(ifcTypeName, out var mapped))
    {
      candidates.Add(mapped);
    }

    var result = _catalogValidator.ValidateFirstOrFallback(candidates, DEFAULT_PROFILE, isProfile: true);
    if (result.Warning != null && ifcTypeName is not null)
    {
      result = (result.Value, result.Warning + $" [ifcTypeName: {ifcTypeName}]");
    }
    return result;
  }

  /// <summary>
  /// The enricher's own captured <c>material</c> (a real IFC material name, e.g. "Ortbeton - bewehrt
  /// Verputzt") wins unconditionally when it's a real Tekla catalog entry. Real-world IFC material
  /// names are commonly generic descriptions rather than a Tekla-catalog-recognized grade string, so
  /// this falls back to a default - but which default depends on whether this element's own captured
  /// profile LOOKS concrete-shaped (the enricher's "D{d}"/"{h}*{w}" parametric convention, produced
  /// only from a rectangular/circular IFC profile - see RevitNativeSchemaEnricher.TryEnrichColumn/
  /// TryEnrichBeam) or not (an unresolved profile, i.e. a steel catalog section like an I/H-beam,
  /// which never gets a parametric string here). Without this distinction every concrete
  /// column/beam whose real material name isn't in Tekla's catalog silently defaulted to a STEEL
  /// material - confirmed live, visibly wrong in the part's own Material field.
  /// </summary>
  private (string Value, string? Warning) ResolveMaterial(DataObject target)
  {
    string? ifcMaterialName = target["material"] as string;
    bool looksLikeConcreteProfile =
      target["profile"] is string rawProfile
      && (rawProfile.StartsWith("D", StringComparison.OrdinalIgnoreCase) || rawProfile.Contains('*'));
    string defaultMaterial = looksLikeConcreteProfile ? DEFAULT_CONCRETE_MATERIAL : DEFAULT_STEEL_MATERIAL;
    return _catalogValidator.ValidateOrFallback(ifcMaterialName, defaultMaterial, isProfile: false);
  }

  public object Convert(object target) => Convert((DataObject)target);
}
