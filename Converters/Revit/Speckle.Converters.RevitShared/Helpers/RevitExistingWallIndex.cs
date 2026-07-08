using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.RevitShared.Settings;

namespace Speckle.Converters.RevitShared.Helpers;

/// <summary>
/// Per-receive-operation (scoped) lookup of existing <c>DB.Wall</c> elements by origin applicationId.
/// A separate class from <see cref="RevitExistingBeamIndex"/> because <c>DB.Wall</c> is a
/// <c>HostObject</c>, not a <c>FamilyInstance</c> - the two can't share one
/// <c>FilteredElementCollector</c> filter. Otherwise mirrors it exactly: lets receive recognize a
/// round-tripped wall as one it already has and move it in place instead of inserting a duplicate,
/// and lets it recognize a previously-managed wall missing from the current payload as removed at
/// the source.
///
/// Each wall is indexed under up to two keys: its own native <c>Element.UniqueId</c> (always, since a
/// Revit-authored wall's own applicationId on send IS this UniqueId when it has never been stamped,
/// so a wall authored natively in Revit and never previously received still matches on its first
/// round-trip through Tekla with no extra bookkeeping needed) and its stamped origin-id (see
/// <see cref="OriginApplicationIdSchema"/>) if present - needed for a wall originally authored in a
/// DIFFERENT app, received into Revit, whose native UniqueId differs from its true cross-app origin id.
///
/// Only walls that carry the origin-id STAMP (i.e. have round-tripped through this mechanism at least
/// once) are ever deletion candidates - one that has never been Speckle-managed is never touched just
/// because this receive didn't mention it, even if it's indexed under its native UniqueId. Built
/// lazily on first use, scanning the document once per receive.
/// </summary>
public class RevitExistingWallIndex(
  IConverterSettingsStore<RevitConversionSettings> settingsStore,
  ILogger<RevitExistingWallIndex> logger
)
{
  private Dictionary<string, DB.Wall>? _byOriginApplicationId;
  private HashSet<DB.Wall>? _managedWalls;
  private readonly HashSet<DB.Wall> _claimed = new();

  public bool TryFindExisting(string? originApplicationId, out DB.Wall? existing)
  {
    existing = null;
    if (string.IsNullOrEmpty(originApplicationId))
    {
      logger.LogInformation("RevitExistingWallIndex: lookup skipped, no origin applicationId on incoming object.");
      return false;
    }

    // string.IsNullOrEmpty's [NotNullWhen(false)] narrowing above isn't picked up reliably on the
    // net48 target's older reference assemblies - originApplicationId is provably non-null here.
    bool found = BuildIndex().TryGetValue(originApplicationId!, out existing);
    if (found)
    {
      _claimed.Add(existing!);
    }
    logger.LogInformation(
      "RevitExistingWallIndex: lookup for origin={OriginApplicationId} -> {Found}",
      originApplicationId,
      found
    );
    return found;
  }

  /// <summary>
  /// Previously Speckle-managed walls (carry the origin-id stamp) that were NOT matched by anything
  /// in this receive's payload - candidates for deletion because they were removed at the source.
  /// </summary>
  public IReadOnlyCollection<DB.Wall> GetDeletionCandidates()
  {
    BuildIndex();
    return _managedWalls!.Where(w => !_claimed.Contains(w)).ToList();
  }

  private Dictionary<string, DB.Wall> BuildIndex()
  {
    if (_byOriginApplicationId is not null)
    {
      return _byOriginApplicationId;
    }

    var index = new Dictionary<string, DB.Wall>();
    var managed = new HashSet<DB.Wall>();
    int scanned = 0;
    using var collector = new DB.FilteredElementCollector(settingsStore.Current.Document);
    var walls = collector.OfClass(typeof(DB.Wall)).Cast<DB.Wall>();

    foreach (DB.Wall wall in walls)
    {
      scanned++;
      index[wall.UniqueId] = wall;
      if (OriginApplicationIdSchema.TryGet(wall, out string? originApplicationId))
      {
        index[originApplicationId] = wall;
        managed.Add(wall);
      }
    }

    logger.LogInformation(
      "RevitExistingWallIndex: scanned {Scanned} walls, indexed {Indexed} keys, {Managed} Speckle-managed.",
      scanned,
      index.Count,
      managed.Count
    );
    _byOriginApplicationId = index;
    _managedWalls = managed;
    return index;
  }
}
