using Speckle.Sdk.Models;

namespace Speckle.Converters.TeklaShared;

public class TeklaObject : Base
{
  public string? Name { get; set; }
  public string? Type { get; set; }
  public Base? Location { get; set; }
  public List<TeklaObject>? Elements { get; set; }
  public Dictionary<string, object?> Properties { get; set; } = new();
  public List<Base>? DisplayValue { get; set; }
  public string? Units { get; set; }

  public TeklaObject() { }
}
