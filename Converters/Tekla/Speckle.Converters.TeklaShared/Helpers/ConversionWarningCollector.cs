namespace Speckle.Converters.TeklaShared.Helpers;

/// <summary>
/// Per-operation collector letting a converter attach non-fatal warning text to the Speckle object
/// it just converted (e.g. "used a default profile because nothing matched"), without failing the
/// conversion. TeklaHostObjectBuilder reads these back after each successful conversion to promote
/// its ReceiveConversionResult from SUCCESS to WARNING.
/// </summary>
public class ConversionWarningCollector
{
  private readonly Dictionary<string, List<string>> _warnings = new();

  public void Add(string? speckleId, string message)
  {
    if (speckleId is null)
    {
      return;
    }

    if (!_warnings.TryGetValue(speckleId, out var list))
    {
      list = new List<string>();
      _warnings[speckleId] = list;
    }
    list.Add(message);
  }

  public IReadOnlyList<string>? Get(string? speckleId) =>
    speckleId != null && _warnings.TryGetValue(speckleId, out var list) ? list : null;
}
