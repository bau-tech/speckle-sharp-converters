using Microsoft.Extensions.Logging;
using Speckle.Connectors.Common.Builders;
using Speckle.Connectors.Common.Caching;
using Speckle.Connectors.Common.Conversion;
using Speckle.Connectors.Common.Operations;
using Speckle.Connectors.TeklaShared.Extensions;
using Speckle.Connectors.TeklaShared.HostApp;
using Speckle.Converters.Common;
using Speckle.Converters.TeklaShared;
using Speckle.Sdk;
using Speckle.Sdk.Logging;
using Speckle.Sdk.Models;
using Speckle.Sdk.Models.Collections;
using Speckle.Sdk.Pipelines.Progress;

namespace Speckle.Connectors.TeklaShared.Operations.Send;

public class TeklaRootObjectBuilder : IRootObjectBuilder<TSM.ModelObject>
{
  private readonly IRootToSpeckleConverter _rootToSpeckleConverter;
  private readonly ISendConversionCache _sendConversionCache;
  private readonly IConverterSettingsStore<TeklaConversionSettings> _converterSettings;
  private readonly SendCollectionManager _sendCollectionManager;
  private readonly ILogger<TeklaRootObjectBuilder> _logger;
  private readonly ISdkActivityFactory _activityFactory;
  private readonly TeklaMaterialUnpacker _materialUnpacker;

  public TeklaRootObjectBuilder(
    IRootToSpeckleConverter rootToSpeckleConverter,
    ISendConversionCache sendConversionCache,
    IConverterSettingsStore<TeklaConversionSettings> converterSettings,
    SendCollectionManager sendCollectionManager,
    ILogger<TeklaRootObjectBuilder> logger,
    ISdkActivityFactory activityFactory,
    TeklaMaterialUnpacker materialUnpacker
  )
  {
    _sendConversionCache = sendConversionCache;
    _converterSettings = converterSettings;
    _sendCollectionManager = sendCollectionManager;
    _rootToSpeckleConverter = rootToSpeckleConverter;
    _logger = logger;
    _activityFactory = activityFactory;
    _materialUnpacker = materialUnpacker;
  }

  public async Task<RootObjectBuilderResult> Build(
    IReadOnlyList<TSM.ModelObject> teklaObjects,
    string projectId,
    IProgress<CardProgress> onOperationProgressed,
    CancellationToken cancellationToken
  )
  {
    using var activity = _activityFactory.Start("Build");

    var model = new TSM.Model();
    string modelName = model.GetInfo().ModelName ?? "Unnamed model";

    Collection rootObjectCollection = new() { name = modelName };
    rootObjectCollection["units"] = _converterSettings.Current.SpeckleUnits;

    // A BooleanPart cut is already nested under its Father Part by ModelObjectToSpeckleConverter's
    // GetSupportedChildren() traversal - if the user's selection ALSO includes the cut directly (e.g.
    // select-all grabs it as its own ModelObject), sending it again here as a top-level object
    // duplicates it (nested copy + extra top-level copy). Mirrors Revit's RevitParentChildRules,
    // scoped to the one case actually observed on the Tekla side so far.
    var selectedGuids = teklaObjects.Select(o => o.Identifier.GUID).ToHashSet();
    var objectsToSend = teklaObjects.Where(o => !IsBooleanChildOfSelectedFather(o, selectedGuids)).ToList();

    List<SendConversionResult> results = new(objectsToSend.Count);
    int count = 0;

    using (var _ = _activityFactory.Start("Convert all"))
    {
      foreach (TSM.ModelObject teklaObject in objectsToSend)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var result = ConvertTeklaObject(teklaObject, rootObjectCollection, projectId);
        results.Add(result);

        ++count;
        onOperationProgressed.Report(new("Converting", (double)count / objectsToSend.Count));
        await Task.Yield();
      }
    }

    if (results.All(x => x.Status == Status.ERROR))
    {
      throw new SpeckleException("Failed to convert all objects.");
    }

    var renderMaterialProxies = _materialUnpacker.UnpackRenderMaterial(objectsToSend);
    if (renderMaterialProxies.Count > 0)
    {
      rootObjectCollection[ProxyKeys.RENDER_MATERIAL] = renderMaterialProxies;
    }

    return new RootObjectBuilderResult(rootObjectCollection, results);
  }

  private SendConversionResult ConvertTeklaObject(
    TSM.ModelObject teklaObject,
    Collection collectionHost,
    string projectId
  )
  {
    string applicationId = teklaObject.GetSpeckleApplicationId();
    string sourceType = teklaObject.GetType().Name;

    try
    {
      Base converted;
      if (_sendConversionCache.TryGetValue(projectId, applicationId, out ObjectReference? value))
      {
        converted = value;
      }
      else
      {
        converted = _rootToSpeckleConverter.Convert(teklaObject);
      }

      var collection = _sendCollectionManager.GetAndCreateObjectHostCollection(teklaObject, collectionHost);

      // Add to host collection
      collection.elements.Add(converted);

      return new(Status.SUCCESS, applicationId, sourceType, converted);
    }
    catch (Exception ex) when (!ex.IsFatal())
    {
      _logger.LogError(ex, "Failed to convert object {SourceType}", sourceType);
      // Also write directly to a temp file so we can diagnose when the logger isn't flushing
      try
      {
        System.IO.File.AppendAllText(
          System.IO.Path.Combine(System.IO.Path.GetTempPath(), "speckle_tekla_send_error.txt"),
          $"[{System.DateTime.Now:HH:mm:ss}] {sourceType} ({applicationId}): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n---\n"
        );
      }
#pragma warning disable CA1031
      catch
      { /* never fail here */
      }
#pragma warning restore CA1031
      return new(Status.ERROR, applicationId, sourceType, null, ex);
    }
  }

  private static bool IsBooleanChildOfSelectedFather(TSM.ModelObject obj, HashSet<Guid> selectedGuids) =>
    obj is TSM.BooleanPart bp && bp.Father is TSM.Part father && selectedGuids.Contains(father.Identifier.GUID);
}
