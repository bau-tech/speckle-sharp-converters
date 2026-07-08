using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Converters.RevitShared.Helpers;
using Speckle.Converters.RevitShared.Helpers.ProfileMapping;
using Speckle.Converters.RevitShared.Settings;
using Speckle.Objects;
using Speckle.Sdk.Common;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.RevitShared.ToHost;

public class FoundationToHostConverter : ITypedConverter<Base, DB.Element>
{
  private readonly IConverterSettingsStore<RevitConversionSettings> _settingsStore;
  private readonly RevitElementTypeResolver _typeResolver;
  private readonly RevitToHostCacheSingleton _cache;
  private readonly StructuralFramingHelper _structuralFramingHelper;
  private readonly ITypedConverter<ICurve, DB.CurveArray> _curveConverter;
  private readonly ITypedConverter<DB.CurveArray, DB.CurveLoop> _curveLoopConverter;
  private readonly TeklaProfileMappingProvider _profileMappingProvider;
  private readonly ILogger<FoundationToHostConverter> _logger;

  public FoundationToHostConverter(
    IConverterSettingsStore<RevitConversionSettings> settingsStore,
    RevitElementTypeResolver typeResolver,
    RevitToHostCacheSingleton cache,
    StructuralFramingHelper structuralFramingHelper,
    ITypedConverter<ICurve, DB.CurveArray> curveConverter,
    ITypedConverter<DB.CurveArray, DB.CurveLoop> curveLoopConverter,
    TeklaProfileMappingProvider profileMappingProvider,
    ILogger<FoundationToHostConverter> logger
  )
  {
    _settingsStore = settingsStore;
    _typeResolver = typeResolver;
    _cache = cache;
    _structuralFramingHelper = structuralFramingHelper;
    _curveConverter = curveConverter;
    _curveLoopConverter = curveLoopConverter;
    _profileMappingProvider = profileMappingProvider;
    _logger = logger;
  }

  public DB.Element Convert(Base target)
  {
    // Wall Foundations (continuous footings hosted on a Wall) are HostObjects, not FamilyInstances -
    // they're created via WallFoundation.Create against the already-received host Wall.
    if (target["location"] is SOG.Line && TryGetHostWall(target) is DB.Wall hostWall)
    {
      return CreateWallFoundation(target, hostWall);
    }

    // Isolated footings (point) and other line-based footings are family instances,
    // created the same way as Beams/Columns. Foundation slabs have a closed boundary instead.
    object? location = target["location"];
    if (location is SOG.Point or SOG.Line)
    {
      DB.FamilyInstance footing = _structuralFramingHelper.Create(
        target,
        DB.BuiltInCategory.OST_StructuralFoundation,
        DB.Structure.StructuralType.Footing
      );
      _structuralFramingHelper.ReapplyPlacementRotation(target, footing);

      // Trust an explicit user mapping (see the profile mapping dialog) completely - the type it
      // points at is already exactly right, so there's nothing to correct. Guessing at
      // width/length/thickness parameter NAMES beyond this is not just unnecessary here but risky:
      // those are TYPE parameters, shared by every instance of that type across the whole model, so
      // a wrong guess wouldn't just mis-size this one footing, it could silently resize every other
      // element already using that type.
      string? profile = target["profile"] as string;
      bool alreadyMapped =
        !string.IsNullOrEmpty(profile)
        && _profileMappingProvider.TryGetFamilyType(
          DB.BuiltInCategory.OST_StructuralFoundation,
          profile!,
          out _,
          out _
        );
      if (!alreadyMapped)
      {
        ApplyFootingDimensions(footing, target, location as SOG.Line);
      }

      return footing;
    }

    return CreateFoundationSlab(target);
  }

  // Autodesk's standard isolated-foundation family templates ("M_Footing-Rectangular" and similar)
  // expose these as type parameters - not BuiltInParameters, since isolated footing thickness has no
  // stable enum value the way e.g. wall/floor thickness does.
  private static readonly string[] s_footingWidthParamNames = ["Width", "b", "Breite"];
  private static readonly string[] s_footingLengthParamNames = ["Length", "Tiefe", "Depth"];
  private static readonly string[] s_footingThicknessParamNames =
  [
    "Foundation Thickness",
    "Thickness",
    "Dicke",
    "Height",
    "h",
    "Höhe",
    "Hoehe",
  ];

  /// <summary>
  /// Sizes a footing from its plan footprint ("{width}*{depth}" profile string) and thickness (the
  /// length of the vertical "beam" Tekla represents a pad footing as - see
  /// RevitFoundationToTeklaConverter.ConvertPadFooting) - neither dimension is captured by
  /// StructuralFramingHelper's generic beam-oriented symbol resolution (deliberately skipped for this
  /// category - see ResolveSymbol). Prefers swapping to an ALREADY-LOADED symbol whose own
  /// width/length/thickness parameters already match (so an existing project type - e.g. a real
  /// "1500x1500x500" footing type - gets reused instead of overriding whatever symbol
  /// FindFamilySymbol's first-available fallback happened to pick); only overrides parameters
  /// directly on the created instance's own symbol as a fallback when no such match exists.
  /// </summary>
  private void ApplyFootingDimensions(DB.FamilyInstance footing, Base target, SOG.Line? originalLine)
  {
    bool hasFootprint = StructuralFramingHelper.TryParseRectangularProfileMm(
      target["profile"] as string,
      out double widthMm,
      out double depthMm
    );

    double? thicknessMm = null;
    if (originalLine is not null)
    {
      DB.CurveArray curveArray = _curveConverter.Convert(originalLine);
      if (curveArray.Size > 0)
      {
        thicknessMm = DB.UnitUtils.ConvertFromInternalUnits(
          curveArray.get_Item(0).ApproximateLength,
          DB.UnitTypeId.Millimeters
        );
      }
    }

    if (
      hasFootprint
      && thicknessMm is { } t
      && FindMatchingFootingSymbol(widthMm, depthMm, t) is { } matchingSymbol
      && footing.Symbol.Id != matchingSymbol.Id
    )
    {
      footing.Symbol = matchingSymbol;
      _logger.LogInformation(
        "FoundationToHostConverter.ApplyFootingDimensions: swapped instance {ElementId} to matching symbol {SymbolName} ({WidthMm}x{DepthMm}x{ThicknessMm}mm)",
        footing.Id,
        matchingSymbol.Name,
        widthMm,
        depthMm,
        t
      );
      return;
    }

    if (hasFootprint)
    {
      TrySetLengthParam(footing, "width", s_footingWidthParamNames, widthMm);
      TrySetLengthParam(footing, "length", s_footingLengthParamNames, depthMm);
    }

    if (thicknessMm is { } thicknessMmValue)
    {
      TrySetLengthParam(footing, "thickness", s_footingThicknessParamNames, thicknessMmValue);
    }
  }

  private const double DIMENSION_TOLERANCE_MM = 0.5;

  private DB.FamilySymbol? FindMatchingFootingSymbol(double widthMm, double depthMm, double thicknessMm) =>
    _typeResolver
      .GetFamilySymbols(DB.BuiltInCategory.OST_StructuralFoundation)
      .FirstOrDefault(symbol =>
        RevitElementTypeResolver.TryGetParamValueMm(symbol, s_footingWidthParamNames, out double w)
        && Math.Abs(w - widthMm) < DIMENSION_TOLERANCE_MM
        && RevitElementTypeResolver.TryGetParamValueMm(symbol, s_footingLengthParamNames, out double l)
        && Math.Abs(l - depthMm) < DIMENSION_TOLERANCE_MM
        && RevitElementTypeResolver.TryGetParamValueMm(symbol, s_footingThicknessParamNames, out double t)
        && Math.Abs(t - thicknessMm) < DIMENSION_TOLERANCE_MM
      );

  private void TrySetLengthParam(
    DB.FamilyInstance instance,
    string dimensionName,
    string[] candidateNames,
    double valueMm
  ) =>
    TrySetLengthParamFeet(
      instance,
      dimensionName,
      candidateNames,
      DB.UnitUtils.ConvertToInternalUnits(valueMm, DB.UnitTypeId.Millimeters)
    );

  private void TrySetLengthParamFeet(
    DB.FamilyInstance instance,
    string dimensionName,
    string[] candidateNames,
    double valueFeet
  )
  {
    foreach (string name in candidateNames)
    {
      DB.Parameter? param = instance.LookupParameter(name) ?? instance.Symbol.LookupParameter(name);
      if (param is { IsReadOnly: false, StorageType: DB.StorageType.Double })
      {
        param.Set(valueFeet);
        _logger.LogInformation(
          "FoundationToHostConverter.ApplyFootingDimensions: set {DimensionName} via parameter {ParamName} to {ValueFeet} ft on instance {ElementId}",
          dimensionName,
          name,
          valueFeet,
          instance.Id
        );
        return;
      }
    }

    _logger.LogWarning(
      "FoundationToHostConverter.ApplyFootingDimensions: no writable parameter found for {DimensionName} among [{Candidates}] on instance {ElementId} - left at the family/type default.",
      dimensionName,
      string.Join(", ", candidateNames),
      instance.Id
    );
  }

  /// <summary>
  /// Looks up the host Wall for <paramref name="target"/> via its captured <c>parentApplicationId</c>
  /// (set on send for FamilyInstance/Opening hosts - WallFoundation is hosted on a Wall in the same way).
  /// Returns null if no host was captured, or if the host hasn't been received as a Wall in this operation.
  /// </summary>
  private DB.Wall? TryGetHostWall(Base target) =>
    target["properties"] is Dictionary<string, object?> properties
    && properties.GetOrDefault("parentApplicationId") is string hostApplicationId
    && _cache.ReceivedElementsByApplicationId.TryGetValue(hostApplicationId, out DB.Element? host)
    && host is DB.Wall wall
      ? wall
      : null;

  private DB.WallFoundation CreateWallFoundation(Base target, DB.Wall hostWall)
  {
    var doc = _settingsStore.Current.Document;

    DB.WallFoundationType wallFoundationType =
      _typeResolver.FindWallFoundationType(target["type"] as string)
      ?? throw new ConversionException("No Wall Foundation types found in the document.");

    DB.WallFoundation wallFoundation = DB.WallFoundation.Create(doc, wallFoundationType.Id, hostWall.Id);

    string cacheKey = target.applicationId ?? target.id.NotNull();
    _cache.ReceivedElementsByApplicationId[cacheKey] = wallFoundation;

    return wallFoundation;
  }

  private DB.Floor CreateFoundationSlab(Base target)
  {
    var doc = _settingsStore.Current.Document;

    if (target["location"] is not ICurve location)
    {
      throw new ConversionException("Native Foundation Slab requires a curve boundary location.");
    }

    DB.CurveArray curveArray = _curveConverter.Convert(location);
    if (curveArray.Size == 0)
    {
      throw new ConversionException("Native Foundation Slab location did not produce any curves.");
    }

    DB.CurveLoop loop = _curveLoopConverter.Convert(curveArray);

    DB.FloorType floorType =
      _typeResolver.FindFoundationSlabType(target["type"] as string)
      ?? throw new ConversionException("No foundation slab FloorTypes found in the document.");

    DB.Level level =
      _typeResolver.FindLevel(target["level"] as string)
      ?? throw new ConversionException("No levels found in the document.");

    DB.Floor floor = DB.Floor.Create(doc, new List<DB.CurveLoop> { loop }, floorType.Id, level.Id);

    if (
      RevitElementPropertyApplicator.TryGetLengthInFeet(
        target,
        "Instance Parameters",
        "FLOOR_HEIGHTABOVELEVEL_PARAM",
        out double heightOffset
      )
    )
    {
      RevitElementPropertyApplicator.TrySetDouble(
        floor,
        DB.BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM,
        heightOffset
      );
    }

    string cacheKey = target.applicationId ?? target.id.NotNull();
    _cache.ReceivedElementsByApplicationId[cacheKey] = floor;

    return floor;
  }

  public object Convert(object target) => Convert((Base)target);
}
