using Speckle.Connectors.Common.Instances;
using Speckle.Connectors.Common.Operations.Receive;
using Speckle.Objects.Data;
using Speckle.Sdk.Common;
using Speckle.Sdk.Models.Collections;
using Speckle.Sdk.Models.Instances;

namespace Speckle.Connectors.Revit.Operations.Receive;

public class FamilyUnpackStrategy : RevitUnpackStrategyBase
{
  private readonly ILocalToGlobalUnpacker _localToGlobalUnpacker;
  private readonly RootObjectUnpacker _rootObjectUnpacker;

  public FamilyUnpackStrategy(ILocalToGlobalUnpacker localToGlobalUnpacker, RootObjectUnpacker rootObjectUnpacker)
  {
    _localToGlobalUnpacker = localToGlobalUnpacker;
    _rootObjectUnpacker = rootObjectUnpacker;
  }

  // RevitObjects in these categories are reconstructed as native FamilyInstances on receive
  // (StructuralFramingHelper). DisplayValueExtractor always attaches an instance transform to
  // FamilyInstance geometry, which turns their display mesh into an InstanceProxy. Without the
  // special-casing below, FilterUnpackedDataObjects (step 8) would drop these RevitObjects
  // entirely - they'd never reach RevitRootToHostConverter for native dispatch, leaving only the
  // bare display mesh to be baked as a transformed DirectShape.
  private static readonly HashSet<string> s_nativeFamilyInstanceCategories = new(StringComparer.OrdinalIgnoreCase)
  {
    "OST_StructuralFraming",
    "OST_StructuralColumns",
    "OST_StructuralFoundation",
  };

  public override UnpackStrategyResult Unpack(RootObjectUnpackerResult unpackedRoot)
  {
    var parentDataObjectMap = new Dictionary<string, DataObject>();
    var displayValueDefinitionIds = new HashSet<string>();

    // 1. Build parent maps and identify definitions used purely for DataObject display values
    PopulateParentDataObjectMap(unpackedRoot, parentDataObjectMap, displayValueDefinitionIds);

    // 1b. Strip display-only InstanceProxies from natively-reconstructed RevitObjects (Beams,
    // Columns, Foundations) so they survive FilterUnpackedDataObjects below, and remember their
    // definitions so the now-orphaned display mesh is fully consumed (step 3) instead of
    // surviving as a duplicate transformed DirectShape.
    var nativeDisplayDefinitionIds = new HashSet<string>();
    foreach (var tc in unpackedRoot.ObjectsToConvert)
    {
      if (
        tc.Current is RevitObject revitObject
        && revitObject["builtInCategory"] as string is { } builtInCategory
        && s_nativeFamilyInstanceCategories.Contains(builtInCategory)
      )
      {
        foreach (var proxy in revitObject.displayValue.OfType<InstanceProxy>())
        {
          nativeDisplayDefinitionIds.Add(proxy.definitionId);
        }

        revitObject.displayValue.RemoveAll(dv => dv is InstanceProxy);
      }
    }

    // 2. Split out standard atomic objects from instance components
    var (atomicObjects, instanceComponents) = _rootObjectUnpacker.SplitAtomicObjectsAndInstances(
      unpackedRoot.ObjectsToConvert
    );

    // 3. Collect true definition geometries to filter out
    var consumedObjectIds = new HashSet<string>();
    if (unpackedRoot.DefinitionProxies != null)
    {
      foreach (var dp in unpackedRoot.DefinitionProxies)
      {
        var defId = dp.applicationId ?? dp.id.NotNull();
        bool isDisplayOnly =
          displayValueDefinitionIds.Contains(defId) || (dp.id != null && displayValueDefinitionIds.Contains(dp.id));
        bool isNativeDisplayOnly =
          nativeDisplayDefinitionIds.Contains(defId) || (dp.id != null && nativeDisplayDefinitionIds.Contains(dp.id));

        if (!isDisplayOnly || isNativeDisplayOnly)
        {
          foreach (var objId in dp.objects)
          {
            consumedObjectIds.Add(objId);
          }
        }
      }
    }

    // 4. Filter out consumed objects
    var filteredAtomicObjects = atomicObjects
      .Where(tc =>
      {
        var appId = tc.Current.applicationId;
        var id = tc.Current.id;
        return (appId == null || !consumedObjectIds.Contains(appId)) && (id == null || !consumedObjectIds.Contains(id));
      })
      .ToList();

    // 5. Prepare true Family instances (ignore the display value proxies)
    var instanceComponentsWithPath = instanceComponents
      .Where(tc => tc.Current is not InstanceProxy proxy || !displayValueDefinitionIds.Contains(proxy.definitionId))
      .Select(tc => (Array.Empty<Collection>(), tc.Current as IInstanceComponent))
      .Where(x => x.Item2 != null)
      .Select(x => (x.Item1, x.Item2!))
      .ToList();

    // 6. Add true definition proxies
    if (unpackedRoot.DefinitionProxies != null)
    {
      var definitions = unpackedRoot
        .DefinitionProxies.Where(proxy =>
        {
          var defId = proxy.applicationId ?? proxy.id.NotNull();
          return !displayValueDefinitionIds.Contains(defId)
            && (proxy.id == null || !displayValueDefinitionIds.Contains(proxy.id));
        })
        .Select(proxy => (Array.Empty<Collection>(), proxy as IInstanceComponent));

      instanceComponentsWithPath.AddRange(definitions);
    }

    // 7. Flatten surviving atomic objects
    var localToGlobalMaps = _localToGlobalUnpacker.Unpack(unpackedRoot.DefinitionProxies, filteredAtomicObjects);

    // 8. Clean out DataObjects using the shared base logic!
    var cleanedMaps = FilterUnpackedDataObjects(localToGlobalMaps);

    return new UnpackStrategyResult(cleanedMaps, instanceComponentsWithPath, parentDataObjectMap);
  }
}
