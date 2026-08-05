using Ara3D.Utils;
using Speckle.Sdk.Common;

namespace Speckle.Converters.IfcShared.StepParsing;

public class StepGraph
{
  public StepDocument Document { get; }

  public readonly Dictionary<uint, StepNode> Lookup = new();

  public StepNode GetNode(uint id) => Lookup[id];

  public IEnumerable<StepNode> Nodes => Lookup.Values;

  public StepGraph(StepDocument doc)
  {
    Document = doc;

    // FIX (not in the original ported code): `RawInstances` is sized to `LineOffsets.Count`, but only
    // the first `NumRawInstances` entries are populated - lines that don't parse into a real STEP
    // instance (headers, comments, ENDSEC;, etc.) leave trailing default(StepRawInstance) slots with
    // Id=0. `doc.GetInstances()` iterates the *whole* backing array with no validity check, so more
    // than one such trailing slot collides on `Lookup.Add(0, ...)`. This never surfaced in the
    // original project because StepGraph/StepNode were unused there (the production path walked
    // `Document.RawInstances` directly with its own `IsValid()` filter - the same guard applied here).
    for (int i = 0; i < doc.NumRawInstances; i++)
    {
      var e = doc.GetInstanceWithDataFromIndex(i);
      var node = new StepNode(this, e);
      Lookup.Add(node.Entity.Id, node);
    }

    foreach (var n in Nodes)
      n.Init();
  }

  public static StepGraph Create(StepDocument doc) => new(doc);

  public string ToValString(StepNode node, int depth) => ToValString(node.Entity.Entity, depth - 1);

  public string ToValString(StepValue value, int depth)
  {
    if (value == null)
      return "";

    switch (value)
    {
      case StepList stepAggregate:
        return $"({stepAggregate.Values.Select(v => ToValString(v, depth)).JoinStringsWithComma()})";

      case StepEntity stepEntity:
        return $"{stepEntity.EntityType}{ToValString(stepEntity.Attributes, depth)}";

      case StepId stepId:
        return depth <= 0 ? "#" : ToValString(GetNode(stepId.Id), depth - 1);

      case StepNumber stepNumber:
      case StepRedeclared stepRedeclared:
      case StepString stepString:
      case StepSymbol stepSymbol:
      case StepUnassigned stepUnassigned:
      default:
        return value.ToString().NotNull();
    }
  }
}
