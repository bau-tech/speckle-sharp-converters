using Speckle.Connectors.DUI.Models.Card;
using Speckle.Converters.TeklaShared;
using Speckle.InterfaceGenerator;

namespace Speckle.Connectors.TeklaShared.Operations.Receive.Settings;

[GenerateAutoInterface]
public class TeklaToHostSettingsManager : ITeklaToHostSettingsManager
{
  public ReceiveMode GetReceiveModeSetting(ModelCard modelCard)
  {
    var modeString = modelCard.Settings?.FirstOrDefault(s => s.Id == ReceiveModeSetting.SETTING_ID)?.Value as string;
    if (modeString is not null && ReceiveModeSetting.ModeMap.TryGetValue(modeString, out ReceiveMode mode))
    {
      return mode;
    }

    return ReceiveModeSetting.DEFAULT_VALUE;
  }
}
