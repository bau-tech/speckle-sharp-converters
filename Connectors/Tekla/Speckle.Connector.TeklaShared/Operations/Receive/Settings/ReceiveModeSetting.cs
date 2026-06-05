using Speckle.Connectors.DUI.Settings;
using Speckle.Converters.TeklaShared;

namespace Speckle.Connectors.TeklaShared.Operations.Receive.Settings;

public class ReceiveModeSetting(ReceiveMode value = ReceiveMode.Native) : ICardSetting
{
  public const string SETTING_ID = "receiveMode";
  public const ReceiveMode DEFAULT_VALUE = ReceiveMode.Native;

  public string? Id { get; set; } = SETTING_ID;
  public string? Title { get; set; } = "Receive Mode";
  public string? Description { get; set; }
  public string? Type { get; set; } = "string";
  public List<string>? Enum { get; set; } = System.Enum.GetNames(typeof(ReceiveMode)).ToList();
  public object? Value { get; set; } = value.ToString();

  public static readonly Dictionary<string, ReceiveMode> ModeMap = System
    .Enum.GetValues(typeof(ReceiveMode))
    .Cast<ReceiveMode>()
    .ToDictionary(v => v.ToString(), v => v);
}
