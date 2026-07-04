namespace Speckle.Converters.TeklaShared.ToHost;

public class TeklaReceiveCache
{
  private readonly Dictionary<string, TSM.ModelObject> _cache = new();

  /// <summary>
  /// Registers <paramref name="obj"/> under both its Speckle hash (<paramref name="speckleId"/>)
  /// and its original host-application identifier (<paramref name="applicationId"/>).
  /// Sub-components (bolts, welds, rebars) reference their parent parts by the original
  /// Tekla GUID stored in <paramref name="applicationId"/>, so both keys must be resolvable.
  /// </summary>
  public void Add(string? speckleId, string? applicationId, TSM.ModelObject obj)
  {
    if (speckleId != null)
    {
      _cache[speckleId] = obj;
    }
    // Also index by applicationId (Tekla GUID) so cross-references from bolts/welds/rebars resolve.
    if (applicationId != null && applicationId != speckleId)
    {
      _cache[applicationId] = obj;
    }
  }

  public TSM.ModelObject? Get(string? id)
  {
    if (id == null)
    {
      return null;
    }
    return _cache.TryGetValue(id, out var obj) ? obj : null;
  }
}
