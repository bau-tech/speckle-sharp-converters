using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.TeklaShared.Helpers;

namespace Speckle.Converters.TeklaShared.ToHost;

/// <summary>
/// Per-receive-operation (scoped) lookup of existing <see cref="TSM.ContourPlate"/> parts in the
/// model by origin applicationId. Mirrors <see cref="TeklaExistingBeamIndex"/> exactly - a separate
/// class since that one is strictly typed/scanned to <see cref="TSM.Beam"/>, not reusable for
/// <see cref="TSM.ContourPlate"/> (floors/slabs). Lets the Floor converter recognize a round-tripped
/// slab as one it already has and update it in place instead of inserting a duplicate.
/// </summary>
public class TeklaExistingContourPlateIndex(
  IConverterSettingsStore<TeklaConversionSettings> settingsStore,
  ILogger<TeklaExistingContourPlateIndex> logger
)
{
  private Dictionary<string, TSM.ContourPlate>? _byOriginApplicationId;
  private HashSet<TSM.ContourPlate>? _managedPlates;
  private readonly HashSet<TSM.ContourPlate> _claimedPlates = new();

  public bool TryFindExisting(string? originApplicationId, out TSM.ContourPlate? existing)
  {
    existing = null;
    if (string.IsNullOrEmpty(originApplicationId))
    {
      logger.LogInformation(
        "TeklaExistingContourPlateIndex: lookup skipped, no origin applicationId on incoming object."
      );
      return false;
    }

    bool found = BuildIndex().TryGetValue(originApplicationId, out existing);
    if (found)
    {
      _claimedPlates.Add(existing!);
    }
    logger.LogInformation(
      "TeklaExistingContourPlateIndex: lookup for origin={OriginApplicationId} -> {Found}",
      originApplicationId,
      found
    );
    return found;
  }

  /// <summary>
  /// Speckle-managed plates (carry the origin-id UDA) that were NOT matched by any object in this
  /// receive's payload - candidates for deletion because they were removed at the source.
  /// </summary>
  public IReadOnlyCollection<TSM.ContourPlate> GetDeletionCandidates()
  {
    BuildIndex();
    return _managedPlates!.Where(p => !_claimedPlates.Contains(p)).ToList();
  }

  private Dictionary<string, TSM.ContourPlate> BuildIndex()
  {
    if (_byOriginApplicationId is not null)
    {
      return _byOriginApplicationId;
    }

    var index = new Dictionary<string, TSM.ContourPlate>();
    var managed = new HashSet<TSM.ContourPlate>();
    int scanned = 0;
    var enumerator = settingsStore
      .Current.Document.GetModelObjectSelector()
      .GetAllObjectsWithType([typeof(TSM.ContourPlate)]);
    while (enumerator.MoveNext())
    {
      scanned++;
      if (enumerator.Current is not TSM.ContourPlate plate)
      {
        continue;
      }

      index[plate.Identifier.GUID.ToString()] = plate;
      if (TeklaOriginIdentifier.TryGet(plate, out string? originApplicationId))
      {
        index[originApplicationId!] = plate;
        managed.Add(plate);
      }
    }

    logger.LogInformation(
      "TeklaExistingContourPlateIndex: scanned {Scanned} plates, indexed {Indexed} keys, {Managed} Speckle-managed.",
      scanned,
      index.Count,
      managed.Count
    );
    _byOriginApplicationId = index;
    _managedPlates = managed;
    return index;
  }
}
