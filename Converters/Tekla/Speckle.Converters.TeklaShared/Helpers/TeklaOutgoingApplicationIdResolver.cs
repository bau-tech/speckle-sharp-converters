namespace Speckle.Converters.TeklaShared.Helpers;

/// <summary>
/// Resolves the applicationId a Tekla part should be emitted with on send, guarding against two
/// distinct parts colliding on the same identity within a single send.
///
/// Native Tekla "Copy" duplicates User-Defined Attributes, so copying a round-tripped part (one
/// carrying the <see cref="TeklaOriginIdentifier"/> origin-id UDA - e.g. a beam originally authored in
/// Revit) copies that UDA too, leaving two distinct real-world parts stamped with the SAME origin
/// applicationId. Left unguarded, both would be sent with an identical applicationId; a receiving
/// app's existing-element index (see the Revit-side <c>RevitExistingBeamIndex</c>) would then match
/// both incoming objects to the SAME existing element, silently discarding one of them instead of
/// creating a second element.
///
/// The first part encountered in a send (keyed by native GUID, so repeat lookups for the same part -
/// e.g. once for its own applicationId and once as another part's child - are idempotent) keeps the
/// origin id; any other part found to share that same origin id falls back to its own native GUID,
/// i.e. it is treated as if it had never round-tripped and will be recognized as a genuinely new
/// element downstream.
/// </summary>
public class TeklaOutgoingApplicationIdResolver
{
  private readonly Dictionary<Guid, string> _resolvedByGuid = new();
  private readonly HashSet<string> _claimedOriginIds = new();

  public string Resolve(TSM.ModelObject modelObject)
  {
    Guid guid = modelObject.Identifier.GUID;
    if (_resolvedByGuid.TryGetValue(guid, out string? resolved))
    {
      return resolved;
    }

    string nativeId = guid.ToString();
    resolved =
      TeklaOriginIdentifier.TryGet(modelObject, out string? originApplicationId) && _claimedOriginIds.Add(originApplicationId)
        ? originApplicationId
        : nativeId;

    _resolvedByGuid[guid] = resolved;
    return resolved;
  }
}
