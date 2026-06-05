namespace Speckle.Converters.TeklaShared.ToHost;

public class TeklaReceiveCache
{
  private readonly Dictionary<string, TSM.ModelObject> _cache = new();

  public void Add(string? id, TSM.ModelObject obj)
  {
    if (id != null)
    {
      _cache[id] = obj;
    }
  }

  public TSM.ModelObject? Get(string id) => _cache.TryGetValue(id, out var obj) ? obj : null;
}
