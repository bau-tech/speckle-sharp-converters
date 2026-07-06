using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.RevitShared.Settings;

namespace Speckle.Converters.RevitShared.Helpers;

/// <summary>
/// Per-receive-operation (scoped) lookup of existing structural FamilyInstances (Beams/Braces -
/// <c>OST_StructuralFraming</c>, Columns - <c>OST_StructuralColumns</c>, point/line Foundations -
/// <c>OST_StructuralFoundation</c>) in the document by origin applicationId. Mirrors
/// <c>TeklaExistingBeamIndex</c> on the Tekla side: lets receive recognize a round-tripped element as
/// one it already has and move it in place instead of inserting a duplicate, and lets it recognize a
/// previously-managed instance that's missing from the current payload as removed at the source.
///
/// Each instance is indexed under up to two keys: its own native <c>Element.UniqueId</c> (always,
/// since a Revit-authored beam's own applicationId on send IS this UniqueId when it has never been
/// stamped, so a beam authored natively in Revit and never previously received still matches on its
/// first round-trip through Tekla with no extra bookkeeping needed) and its stamped origin-id
/// (<see cref="OriginApplicationIdSchema"/>) if present - needed for a beam originally authored in a
/// DIFFERENT app, received into Revit, whose native UniqueId differs from its true cross-app origin id.
///
/// Only instances that carry the origin-id STAMP (i.e. have round-tripped through this mechanism at
/// least once) are ever deletion candidates - one that has never been Speckle-managed is never touched
/// just because this receive didn't mention it, even if it's indexed under its native UniqueId.
/// Built lazily on first use, scanning the document once per receive.
/// </summary>
public class RevitExistingBeamIndex(
  IConverterSettingsStore<RevitConversionSettings> settingsStore,
  ILogger<RevitExistingBeamIndex> logger
)
{
  private static readonly ICollection<DB.BuiltInCategory> s_categories =
  [
    DB.BuiltInCategory.OST_StructuralFraming,
    DB.BuiltInCategory.OST_StructuralColumns,
    DB.BuiltInCategory.OST_StructuralFoundation,
  ];

  private Dictionary<string, DB.FamilyInstance>? _byOriginApplicationId;
  private HashSet<DB.FamilyInstance>? _managedInstances;
  private readonly HashSet<DB.FamilyInstance> _claimed = new();

  public bool TryFindExisting(string? originApplicationId, out DB.FamilyInstance? existing)
  {
    existing = null;
    if (string.IsNullOrEmpty(originApplicationId))
    {
      logger.LogInformation("RevitExistingBeamIndex: lookup skipped, no origin applicationId on incoming object.");
      return false;
    }

    bool found = BuildIndex().TryGetValue(originApplicationId, out existing);
    if (found)
    {
      _claimed.Add(existing!);
    }
    logger.LogInformation(
      "RevitExistingBeamIndex: lookup for origin={OriginApplicationId} -> {Found}",
      originApplicationId,
      found
    );
    return found;
  }

  /// <summary>
  /// Previously Speckle-managed beams (carry the origin-id stamp) that were NOT matched by anything
  /// in this receive's payload - candidates for deletion because they were removed at the source.
  /// </summary>
  public IReadOnlyCollection<DB.FamilyInstance> GetDeletionCandidates()
  {
    BuildIndex();
    return _managedInstances!.Where(e => !_claimed.Contains(e)).ToList();
  }

  private Dictionary<string, DB.FamilyInstance> BuildIndex()
  {
    if (_byOriginApplicationId is not null)
    {
      return _byOriginApplicationId;
    }

    var index = new Dictionary<string, DB.FamilyInstance>();
    var managed = new HashSet<DB.FamilyInstance>();
    int scanned = 0;
    using var collector = new DB.FilteredElementCollector(settingsStore.Current.Document);
    using var categoryFilter = new DB.ElementMulticategoryFilter(s_categories);
    var instances = collector.OfClass(typeof(DB.FamilyInstance)).WherePasses(categoryFilter).Cast<DB.FamilyInstance>();

    foreach (DB.FamilyInstance instance in instances)
    {
      scanned++;
      index[instance.UniqueId] = instance;
      if (OriginApplicationIdSchema.TryGet(instance, out string? originApplicationId))
      {
        index[originApplicationId] = instance;
        managed.Add(instance);
      }
    }

    logger.LogInformation(
      "RevitExistingBeamIndex: scanned {Scanned} instances, indexed {Indexed} keys, {Managed} Speckle-managed.",
      scanned,
      index.Count,
      managed.Count
    );
    _byOriginApplicationId = index;
    _managedInstances = managed;
    return index;
  }
}
