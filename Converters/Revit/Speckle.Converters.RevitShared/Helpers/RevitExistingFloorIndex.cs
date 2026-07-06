using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.RevitShared.Settings;

namespace Speckle.Converters.RevitShared.Helpers;

/// <summary>
/// Per-receive-operation (scoped) lookup of existing <c>DB.Floor</c> elements by origin applicationId.
/// Mirrors <see cref="RevitExistingWallIndex"/> exactly (a separate class since <c>DB.Floor</c> is a
/// distinct <c>HostObject</c> collector shape, not a <c>FamilyInstance</c>) - but unlike Walls/Beams,
/// a matched Floor is never repositioned in place (Revit's <c>DB.Floor</c> has no single-property
/// boundary setter the way <c>DB.LocationCurve.Curve</c> gives beams/walls) - a match here means
/// "delete this one, create a fresh one with the same stamp" (see FloorToHostConverter).
/// </summary>
public class RevitExistingFloorIndex(
  IConverterSettingsStore<RevitConversionSettings> settingsStore,
  ILogger<RevitExistingFloorIndex> logger
)
{
  private Dictionary<string, DB.Floor>? _byOriginApplicationId;
  private HashSet<DB.Floor>? _managedFloors;
  private readonly HashSet<DB.Floor> _claimed = new();

  public bool TryFindExisting(string? originApplicationId, out DB.Floor? existing)
  {
    existing = null;
    if (string.IsNullOrEmpty(originApplicationId))
    {
      logger.LogInformation("RevitExistingFloorIndex: lookup skipped, no origin applicationId on incoming object.");
      return false;
    }

    bool found = BuildIndex().TryGetValue(originApplicationId, out existing);
    if (found)
    {
      _claimed.Add(existing!);
    }
    logger.LogInformation(
      "RevitExistingFloorIndex: lookup for origin={OriginApplicationId} -> {Found}",
      originApplicationId,
      found
    );
    return found;
  }

  /// <summary>
  /// Previously Speckle-managed floors (carry the origin-id stamp) that were NOT matched by anything
  /// in this receive's payload - candidates for deletion because they were removed at the source.
  /// </summary>
  public IReadOnlyCollection<DB.Floor> GetDeletionCandidates()
  {
    BuildIndex();
    return _managedFloors!.Where(f => !_claimed.Contains(f)).ToList();
  }

  private Dictionary<string, DB.Floor> BuildIndex()
  {
    if (_byOriginApplicationId is not null)
    {
      return _byOriginApplicationId;
    }

    var index = new Dictionary<string, DB.Floor>();
    var managed = new HashSet<DB.Floor>();
    int scanned = 0;
    using var collector = new DB.FilteredElementCollector(settingsStore.Current.Document);
    var floors = collector.OfClass(typeof(DB.Floor)).Cast<DB.Floor>();

    foreach (DB.Floor floor in floors)
    {
      scanned++;
      index[floor.UniqueId] = floor;
      if (OriginApplicationIdSchema.TryGet(floor, out string? originApplicationId))
      {
        index[originApplicationId] = floor;
        managed.Add(floor);
      }
    }

    logger.LogInformation(
      "RevitExistingFloorIndex: scanned {Scanned} floors, indexed {Indexed} keys, {Managed} Speckle-managed.",
      scanned,
      index.Count,
      managed.Count
    );
    _byOriginApplicationId = index;
    _managedFloors = managed;
    return index;
  }
}
