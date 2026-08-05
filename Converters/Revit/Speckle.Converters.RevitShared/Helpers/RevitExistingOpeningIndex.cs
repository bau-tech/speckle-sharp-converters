using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.RevitShared.Settings;

namespace Speckle.Converters.RevitShared.Helpers;

/// <summary>
/// Per-receive-operation (scoped) lookup of existing <c>DB.Opening</c> elements by origin
/// applicationId. Mirrors <see cref="RevitExistingFloorIndex"/> - a matched Opening is never edited
/// in place (Revit's <c>DB.Opening</c> boundary isn't editable post-creation the way
/// <c>DB.LocationCurve.Curve</c> is for beams/walls), so a match means "delete this one, create a
/// fresh one with the same stamp" (see OpeningToHostConverter). Without this, every receive of the
/// same source opening (e.g. repeated Tekla-to-Revit round-trips while testing) creates a brand-new
/// native Opening, stacking duplicate cuts in the wall/floor with each resend.
/// </summary>
public class RevitExistingOpeningIndex(
  IConverterSettingsStore<RevitConversionSettings> settingsStore,
  ILogger<RevitExistingOpeningIndex> logger
)
{
  private Dictionary<string, DB.Opening>? _byOriginApplicationId;
  private HashSet<DB.Opening>? _managedOpenings;
  private readonly HashSet<DB.Opening> _claimed = new();

  public bool TryFindExisting(string? originApplicationId, out DB.Opening? existing)
  {
    existing = null;
    if (string.IsNullOrEmpty(originApplicationId))
    {
      logger.LogInformation("RevitExistingOpeningIndex: lookup skipped, no origin applicationId on incoming object.");
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
      "RevitExistingOpeningIndex: lookup for origin={OriginApplicationId} -> {Found}",
      originApplicationId,
      found
    );
    return found;
  }

  /// <summary>
  /// Previously Speckle-managed openings (carry the origin-id stamp) that were NOT matched by
  /// anything in this receive's payload - candidates for deletion because they were removed at the
  /// source.
  /// </summary>
  public IReadOnlyCollection<DB.Opening> GetDeletionCandidates()
  {
    BuildIndex();
    return _managedOpenings!.Where(o => !_claimed.Contains(o)).ToList();
  }

  private Dictionary<string, DB.Opening> BuildIndex()
  {
    if (_byOriginApplicationId is not null)
    {
      return _byOriginApplicationId;
    }

    var index = new Dictionary<string, DB.Opening>();
    var managed = new HashSet<DB.Opening>();
    int scanned = 0;
    using var collector = new DB.FilteredElementCollector(settingsStore.Current.Document);
    var openings = collector.OfClass(typeof(DB.Opening)).Cast<DB.Opening>();

    foreach (DB.Opening opening in openings)
    {
      scanned++;
      index[opening.UniqueId] = opening;
      if (OriginApplicationIdSchema.TryGet(opening, out string? originApplicationId))
      {
        index[originApplicationId] = opening;
        managed.Add(opening);
      }
    }

    logger.LogInformation(
      "RevitExistingOpeningIndex: scanned {Scanned} openings, indexed {Indexed} keys, {Managed} Speckle-managed.",
      scanned,
      index.Count,
      managed.Count
    );
    _byOriginApplicationId = index;
    _managedOpenings = managed;
    return index;
  }
}
