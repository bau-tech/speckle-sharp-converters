using Speckle.Connectors.DUI.Settings;
using Speckle.Converters.RevitShared.Settings;

namespace Speckle.Connectors.Revit.Operations.Receive.Settings;

public class ReceiveModeSetting(ReceiveMode value = ReceiveMode.DirectShape) : ICardSetting
{
  public const string SETTING_ID = "receiveMode";
  public const ReceiveMode DEFAULT_VALUE = ReceiveMode.DirectShape;

  public string? Id { get; set; } = SETTING_ID;
  public string? Title { get; set; } = "Receive Mode";
  public string? Type { get; set; } = "string";
  public List<string>? Enum { get; set; } = System.Enum.GetNames(typeof(ReceiveMode)).ToList();
  public object? Value { get; set; } = value.ToString();

  public static readonly Dictionary<string, ReceiveMode> ModeMap = System
    .Enum.GetValues(typeof(ReceiveMode))
    .Cast<ReceiveMode>()
    .ToDictionary(v => v.ToString(), v => v);
}
