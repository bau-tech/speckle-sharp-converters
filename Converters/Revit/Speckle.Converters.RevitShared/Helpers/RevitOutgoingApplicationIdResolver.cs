namespace Speckle.Converters.RevitShared.Helpers;

/// <summary>
/// Resolves the applicationId a Revit element should be emitted with on send, guarding against two
/// distinct elements colliding on the same identity within a single send.
///
/// Native Revit "Copy" duplicates ExtensibleStorage data, so copying a round-tripped element (one
/// carrying the <see cref="OriginApplicationIdSchema"/> origin-id stamp - e.g. a beam originally
/// authored in Tekla) copies that stamp too, leaving two distinct real-world elements stamped with the
/// SAME origin applicationId. Left unguarded, both would be sent with an identical applicationId; a
/// receiving app's existing-element index (see the Tekla-side <c>TeklaExistingBeamIndex</c>) would then
/// match both incoming objects to the SAME existing element, silently discarding one of them instead of
/// creating a second element.
///
/// The first element encountered in a send (keyed by <c>UniqueId</c>, so repeat lookups for the same
/// element are idempotent) keeps the origin id; any other element found to share that same origin id
/// falls back to its own <c>UniqueId</c>, i.e. it is treated as if it had never round-tripped and will
/// be recognized as a genuinely new element downstream.
/// </summary>
public class RevitOutgoingApplicationIdResolver
{
  private readonly Dictionary<string, string> _resolvedByUniqueId = new();
  private readonly HashSet<string> _claimedOriginIds = new();

  public string Resolve(DB.Element element)
  {
    if (_resolvedByUniqueId.TryGetValue(element.UniqueId, out string? resolved))
    {
      return resolved;
    }

    resolved =
      OriginApplicationIdSchema.TryGet(element, out string? originApplicationId) && _claimedOriginIds.Add(originApplicationId)
        ? originApplicationId
        : element.UniqueId;

    _resolvedByUniqueId[element.UniqueId] = resolved;
    return resolved;
  }
}
