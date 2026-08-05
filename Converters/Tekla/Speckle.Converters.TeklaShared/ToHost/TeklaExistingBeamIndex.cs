using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.TeklaShared.Helpers;

namespace Speckle.Converters.TeklaShared.ToHost;

/// <summary>
/// Per-receive-operation (scoped) lookup of existing <see cref="TSM.Beam"/> parts in the model by
/// origin applicationId. Lets the Beam converter recognize a round-tripped element as one it already
/// has and update it in place instead of inserting a duplicate. Built lazily on first use, scanning
/// the model once per receive.
///
/// Each beam is indexed under up to two keys: its own native Tekla GUID (<c>Identifier.GUID</c> -
/// always, since a Tekla-authored beam's own applicationId on send IS this GUID, so a beam that was
/// authored in Tekla and never previously received still matches on its first round-trip through
/// Revit with no extra bookkeeping needed) and its stamped origin-id UDA if present (see
/// <see cref="TeklaOriginIdentifier"/> - needed for a beam that was originally authored in a DIFFERENT
/// app, received into Tekla, and is now round-tripping again, since its native Tekla GUID differs
/// from its true cross-app origin id in that case).
///
/// Also tracks which indexed beams carry the origin-id UDA ("Speckle-managed" - has round-tripped
/// through this mechanism at least once) and which were actually matched during this receive, so
/// <see cref="GetDeletionCandidates"/> can report Speckle-managed beams that were NOT present in this
/// receive's payload - i.e. deleted at the source. Beams that have never round-tripped (no UDA) are
/// never deletion candidates, even if unmatched - only content Speckle has actually touched before is
/// eligible for delete-on-source-removal, so unrelated native Tekla content is never at risk.
/// </summary>
public class TeklaExistingBeamIndex(
  IConverterSettingsStore<TeklaConversionSettings> settingsStore,
  ILogger<TeklaExistingBeamIndex> logger
)
{
  private Dictionary<string, TSM.Beam>? _byOriginApplicationId;
  private HashSet<TSM.Beam>? _managedBeams;
  private readonly HashSet<TSM.Beam> _claimedBeams = new();

  public bool TryFindExisting(string? originApplicationId, out TSM.Beam? existing)
  {
    existing = null;
    if (string.IsNullOrEmpty(originApplicationId))
    {
      logger.LogInformation("TeklaExistingBeamIndex: lookup skipped, no origin applicationId on incoming object.");
      return false;
    }

    bool found = BuildIndex().TryGetValue(originApplicationId, out existing);
    if (found)
    {
      _claimedBeams.Add(existing!);
    }
    logger.LogInformation(
      "TeklaExistingBeamIndex: lookup for origin={OriginApplicationId} -> {Found}",
      originApplicationId,
      found
    );
    return found;
  }

  /// <summary>
  /// Speckle-managed beams (carry the origin-id UDA) that were NOT matched by any object in this
  /// receive's payload - candidates for deletion because they were removed at the source.
  /// </summary>
  public IReadOnlyCollection<TSM.Beam> GetDeletionCandidates()
  {
    BuildIndex();
    return _managedBeams!.Where(b => !_claimedBeams.Contains(b)).ToList();
  }

  private Dictionary<string, TSM.Beam> BuildIndex()
  {
    if (_byOriginApplicationId is not null)
    {
      return _byOriginApplicationId;
    }

    var index = new Dictionary<string, TSM.Beam>();
    var managed = new HashSet<TSM.Beam>();
    int scanned = 0;
    var enumerator = settingsStore.Current.Document.GetModelObjectSelector().GetAllObjectsWithType([typeof(TSM.Beam)]);
    while (enumerator.MoveNext())
    {
      scanned++;
      if (enumerator.Current is not TSM.Beam beam)
      {
        continue;
      }

      index[beam.Identifier.GUID.ToString()] = beam;
      if (TeklaOriginIdentifier.TryGet(beam, out string? originApplicationId))
      {
        index[originApplicationId!] = beam;
        managed.Add(beam);
      }
    }

    logger.LogInformation(
      "TeklaExistingBeamIndex: scanned {Scanned} beams, indexed {Indexed} keys, {Managed} Speckle-managed.",
      scanned,
      index.Count,
      managed.Count
    );
    _byOriginApplicationId = index;
    _managedBeams = managed;
    return index;
  }
}
