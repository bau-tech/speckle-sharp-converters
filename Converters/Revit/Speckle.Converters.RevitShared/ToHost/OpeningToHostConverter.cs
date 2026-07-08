using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Converters.RevitShared.Helpers;
using Speckle.Converters.RevitShared.Settings;
using Speckle.Objects;
using Speckle.Sdk.Common;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.RevitShared.ToHost;

public class OpeningToHostConverter : ITypedConverter<Base, DB.Element>
{
  private readonly IConverterSettingsStore<RevitConversionSettings> _settingsStore;
  private readonly RevitElementTypeResolver _typeResolver;
  private readonly RevitToHostCacheSingleton _cache;
  private readonly ITypedConverter<ICurve, DB.CurveArray> _curveConverter;
  private readonly RevitExistingOpeningIndex _existingOpeningIndex;
  private readonly ILogger<OpeningToHostConverter> _logger;

  public OpeningToHostConverter(
    IConverterSettingsStore<RevitConversionSettings> settingsStore,
    RevitElementTypeResolver typeResolver,
    RevitToHostCacheSingleton cache,
    ITypedConverter<ICurve, DB.CurveArray> curveConverter,
    RevitExistingOpeningIndex existingOpeningIndex,
    ILogger<OpeningToHostConverter> logger
  )
  {
    _settingsStore = settingsStore;
    _typeResolver = typeResolver;
    _cache = cache;
    _curveConverter = curveConverter;
    _existingOpeningIndex = existingOpeningIndex;
    _logger = logger;
  }

  public DB.Element Convert(Base target)
  {
    if (target["location"] is not ICurve location)
    {
      throw new ConversionException("Native Opening requires a curve boundary location.");
    }

    DB.CurveArray curveArray = _curveConverter.Convert(location);
    if (curveArray.Size == 0)
    {
      throw new ConversionException("Native Opening location did not produce any curves.");
    }

    string category = target["builtInCategory"] as string ?? string.Empty;

    // A matched opening whose origin applicationId was already received in this document is an
    // update, not a new element - delete the old one first. Revit's DB.Opening boundary isn't
    // editable post-creation, so unlike Walls/Beams this is always delete-and-recreate (mirrors
    // FloorToHostConverter's fallback path). Without this, every receive of the same source opening
    // (e.g. repeated round-trip testing) stacks a brand-new duplicate Opening in the host.
    string cacheKey = target.applicationId ?? target.id.NotNull();
    if (
      _existingOpeningIndex.TryFindExisting(cacheKey, out DB.Opening? existingOpening)
      && existingOpening!.IsValidObject
    )
    {
      _settingsStore.Current.Document.Delete(existingOpening.Id);
      _logger.LogInformation(
        "OpeningToHostConverter.Convert: deleted existing opening {ElementId} for applicationId={ApplicationId} (delete-and-recreate update).",
        existingOpening.Id,
        cacheKey
      );
    }

    // Shaft openings span multiple levels and have no host element - reconstruct via the
    // bottom/top level constraints instead of the Wall/HostObject overloads.
    DB.Opening opening = category.Equals("OST_ShaftOpening", StringComparison.OrdinalIgnoreCase)
      ? CreateShaftOpening(target, curveArray)
      : CreateHostedOpening(target, curveArray);

    _cache.ReceivedElementsByApplicationId[cacheKey] = opening;
    OriginApplicationIdSchema.TrySet(opening, cacheKey, _logger);
    _logger.LogInformation(
      "OpeningToHostConverter.Convert: CREATED new opening {ElementId} for applicationId={ApplicationId}",
      opening.Id,
      cacheKey
    );

    return opening;
  }

  private DB.Opening CreateHostedOpening(Base target, DB.CurveArray curveArray)
  {
    var doc = _settingsStore.Current.Document;

    if (
      target["properties"] is not Dictionary<string, object?> properties
      || properties.GetOrDefault("parentApplicationId") is not string hostApplicationId
    )
    {
      throw new ConversionException("Native Opening requires a host element reference.");
    }

    if (!_cache.ReceivedElementsByApplicationId.TryGetValue(hostApplicationId, out DB.Element? host))
    {
      throw new ConversionException($"Host element '{hostApplicationId}' has not been received yet.");
    }

    _logger.LogInformation(
      "OpeningToHostConverter.CreateHostedOpening: hostApplicationId={HostApplicationId} resolved host type={HostType} id={HostId} valid={HostValid}",
      hostApplicationId,
      host.GetType().FullName,
      host.Id,
      host.IsValidObject
    );

    if (host is DB.Wall wall)
    {
      (DB.XYZ min, DB.XYZ max) = GetBoundingBoxCorners(curveArray);
      DB.Opening wallOpening = doc.Create.NewOpening(wall, min, max);
      _logger.LogInformation(
        "OpeningToHostConverter.CreateHostedOpening: created via Wall overload, opening id={OpeningId} host id={HostId}",
        wallOpening.Id,
        wall.Id
      );
      return wallOpening;
    }

    DB.Opening genericOpening = doc.Create.NewOpening(host, curveArray, true);
    _logger.LogInformation(
      "OpeningToHostConverter.CreateHostedOpening: created via generic host overload (host was NOT a DB.Wall), opening id={OpeningId} host type={HostType} host id={HostId}",
      genericOpening.Id,
      host.GetType().FullName,
      host.Id
    );
    return genericOpening;
  }

  private DB.Opening CreateShaftOpening(Base target, DB.CurveArray curveArray)
  {
    var doc = _settingsStore.Current.Document;

    if (
      !RevitElementPropertyApplicator.TryGetLevelReferenceName(
        target,
        "Instance Parameters",
        "WALL_BASE_CONSTRAINT",
        out string? bottomLevelName
      )
      || bottomLevelName is null
    )
    {
      throw new ConversionException("Native Shaft Opening requires a Base Constraint level.");
    }

    if (
      !RevitElementPropertyApplicator.TryGetLevelReferenceName(
        target,
        "Instance Parameters",
        "WALL_HEIGHT_TYPE",
        out string? topLevelName
      )
      || topLevelName is null
    )
    {
      throw new ConversionException("Native Shaft Opening requires a Top Constraint level.");
    }

    DB.Level bottomLevel =
      _typeResolver.FindLevel(bottomLevelName) ?? throw new ConversionException("No levels found in the document.");
    DB.Level topLevel =
      _typeResolver.FindLevel(topLevelName) ?? throw new ConversionException("No levels found in the document.");

    DB.Opening shaft = doc.Create.NewOpening(bottomLevel, topLevel, curveArray);

    if (
      RevitElementPropertyApplicator.TryGetLengthInFeet(
        target,
        "Instance Parameters",
        "WALL_BASE_OFFSET",
        out double baseOffset
      )
    )
    {
      RevitElementPropertyApplicator.TrySetDouble(shaft, DB.BuiltInParameter.WALL_BASE_OFFSET, baseOffset);
    }

    if (
      RevitElementPropertyApplicator.TryGetLengthInFeet(
        target,
        "Instance Parameters",
        "WALL_TOP_OFFSET",
        out double topOffset
      )
    )
    {
      RevitElementPropertyApplicator.TrySetDouble(shaft, DB.BuiltInParameter.WALL_TOP_OFFSET, topOffset);
    }

    return shaft;
  }

  private static (DB.XYZ Min, DB.XYZ Max) GetBoundingBoxCorners(DB.CurveArray curveArray)
  {
    double minX = double.MaxValue;
    double minY = double.MaxValue;
    double minZ = double.MaxValue;
    double maxX = double.MinValue;
    double maxY = double.MinValue;
    double maxZ = double.MinValue;

    foreach (DB.Curve curve in curveArray)
    {
      foreach (DB.XYZ point in new[] { curve.GetEndPoint(0), curve.GetEndPoint(1) })
      {
        minX = Math.Min(minX, point.X);
        minY = Math.Min(minY, point.Y);
        minZ = Math.Min(minZ, point.Z);
        maxX = Math.Max(maxX, point.X);
        maxY = Math.Max(maxY, point.Y);
        maxZ = Math.Max(maxZ, point.Z);
      }
    }

    return (new DB.XYZ(minX, minY, minZ), new DB.XYZ(maxX, maxY, maxZ));
  }

  public object Convert(object target) => Convert((Base)target);
}
