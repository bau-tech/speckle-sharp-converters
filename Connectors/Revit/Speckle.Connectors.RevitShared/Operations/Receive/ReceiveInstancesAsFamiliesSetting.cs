using Speckle.Connectors.DUI.Settings;

namespace Speckle.Connectors.Revit.Operations.Receive;

public class ReceiveInstancesAsFamiliesSetting(bool value = ReceiveInstancesAsFamiliesSetting.DEFAULT_VALUE)
  : ICardSetting
{
  public const string SETTING_ID = "receiveInstancesAsFamiliesSetting";
  public const bool DEFAULT_VALUE = true;

  public string? Id { get; set; } = SETTING_ID;
  public string? Title { get; set; } = "Receive as Native Elements";
  public string? Description { get; set; } =
    "Bake incoming blocks as Revit Families, and reconstruct supported categories (Walls, Floors, Beams, Columns, Grids, Foundations, Roofs, Openings, etc.) as native Revit elements instead of DirectShapes. Disable for faster receive as DirectShapes/DirectShapes only.";
  public string? Type { get; set; } = "boolean";
  public object? Value { get; set; } = value;
  public List<string>? Enum { get; set; }
}
