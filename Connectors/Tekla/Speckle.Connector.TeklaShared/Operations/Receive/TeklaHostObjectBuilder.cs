using Speckle.Connectors.Common.Builders;
using Speckle.Connectors.Common.Conversion;
using Speckle.Connectors.Common.Operations;
using Speckle.Converters.Common;
using Speckle.Converters.TeklaShared;
using Speckle.Converters.TeklaShared.ToHost;
using Speckle.Sdk.Models;
using Tekla.Structures.Model;
using SystemTask = System.Threading.Tasks.Task;

namespace Speckle.Connectors.TeklaShared.Operations.Receive;

public class TeklaHostObjectBuilder : IHostObjectBuilder
{
  private readonly IRootToHostConverter _converter;
  private readonly Model _teklaModel;
  private readonly SubComponentToHostConverter _subComponentConverter;
  private readonly TeklaReceiveCache _receiveCache;

  public TeklaHostObjectBuilder(
    IRootToHostConverter converter,
    Model teklaModel,
    SubComponentToHostConverter subComponentConverter,
    TeklaReceiveCache receiveCache
  )
  {
    _converter = converter;
    _teklaModel = teklaModel;
    _subComponentConverter = subComponentConverter;
    _receiveCache = receiveCache;
  }

  public Task<HostObjectBuilderResult> Build(
    Base rootObject,
    string projectName,
    string modelName,
    IProgress<CardProgress> onOperationProgressed,
    CancellationToken cancellationToken
  )
  {
    List<string> bakedObjectIds = new();
    List<ReceiveConversionResult> results = new();

    // Flatten the root object to find all objects to convert
    // For now, let's just look at 'elements' collection
    var elements = rootObject["elements"] as List<object>;
    if (elements == null && rootObject is Speckle.Sdk.Models.Collections.Collection col)
    {
      elements = col.elements.Cast<object>().ToList();
    }

    if (elements != null)
    {
      var speckleObjects = elements.OfType<Base>().ToList();

      // Pass 1: Build Main Parts
      int count = 0;
      foreach (var speckleObject in speckleObjects)
      {
        cancellationToken.ThrowIfCancellationRequested();
        // Skip sub-components in the first pass
        if (IsSubComponent(speckleObject))
        {
          continue;
        }

        try
        {
          var result = _converter.Convert(speckleObject);
          if (result is ModelObject mo)
          {
            bakedObjectIds.Add(mo.Identifier.ToString());
            results.Add(
              new ReceiveConversionResult(Status.SUCCESS, speckleObject, mo.Identifier.ToString(), mo.GetType().Name)
            );
          }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
          results.Add(new ReceiveConversionResult(Status.ERROR, speckleObject, null, null, ex));
        }

        onOperationProgressed.Report(new CardProgress("Building Main Parts", (double)++count / speckleObjects.Count));
      }

      // Pass 2: Build Sub-Components (Bolts, Welds, Cuts)
      count = 0;
      foreach (var speckleObject in speckleObjects)
      {
        cancellationToken.ThrowIfCancellationRequested();
        if (speckleObject is TeklaObject teklaObject && teklaObject.Elements != null)
        {
          var id = teklaObject.id ?? teklaObject.applicationId;
          if (id != null)
          {
            var parentPart = _receiveCache.Get(id);

            if (parentPart != null)
            {
              foreach (var child in teklaObject.Elements)
              {
                if (child is TeklaObject childTekla && IsSubComponent(childTekla))
                {
                  _subComponentConverter.ConvertAndAttach(childTekla, parentPart);
                }
              }
            }
          }
        }
        onOperationProgressed.Report(new CardProgress("Building Connections", (double)++count / speckleObjects.Count));
      }
    }

    _teklaModel.CommitChanges();

    return SystemTask.FromResult(new HostObjectBuilderResult(bakedObjectIds, results));
  }

  private bool IsSubComponent(Base obj)
  {
    if (obj is TeklaObject to)
    {
      return to.Type == "BoltGroup"
        || to.Type == "Weld"
        || to.Type == "Fitting"
        || to.Type == "BooleanPart"
        || to.Type == "SingleRebar"
        || to.Type == "RebarGroup"
        || to.Type == "RebarMesh";
    }
    return false;
  }
}
