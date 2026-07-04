using Microsoft.Extensions.Logging;
using Speckle.Connectors.DUI.Models.Card;
using Speckle.Converters.TeklaShared;
using Speckle.InterfaceGenerator;

namespace Speckle.Connectors.TeklaShared.Operations.Receive.Settings;

[GenerateAutoInterface]
public class TeklaToHostSettingsManager : ITeklaToHostSettingsManager
{
  private readonly ILogger<TeklaToHostSettingsManager> _logger;

  public TeklaToHostSettingsManager(ILogger<TeklaToHostSettingsManager> logger)
  {
    _logger = logger;
  }

  /// <summary>
  /// Determines the <see cref="ReceiveMode"/> for a model card.
  /// When the user has explicitly chosen a mode in the UI that takes priority.
  /// When the setting is absent the source application of the selected version is used
  /// to auto-select the best mode:
  /// <list type="bullet">
  ///   <item>Any source (Tekla, Revit, …) → <see cref="ReceiveMode.Native"/> — the converter
  ///   chain routes TeklaObjects and BuiltElements independently, so Native always works.</item>
  ///   <item>Unknown / missing source → <see cref="ReceiveMode.Native"/> (default).</item>
  /// </list>
  /// Set mode to <see cref="ReceiveMode.Generic"/> only to force placeholder geometry when
  /// native conversion is known to fail for a specific source.
  /// </summary>
  public ReceiveMode GetReceiveModeSetting(ModelCard modelCard)
  {
    // Explicit UI setting takes priority.
    var modeString = modelCard.Settings?.FirstOrDefault(s => s.Id == ReceiveModeSetting.SETTING_ID)?.Value as string;
    if (modeString is not null && ReceiveModeSetting.ModeMap.TryGetValue(modeString, out ReceiveMode explicitMode))
    {
      return explicitMode;
    }

    // Auto-detect from source application of the version being received.
    string sourceApp = (modelCard as ReceiverModelCard)?.SelectedVersionSourceApp ?? string.Empty;

    _logger.LogInformation(
      "Auto-detecting Tekla receive mode for {ModelCardId}. Source application: '{SourceApp}'",
      modelCard.ModelCardId,
      string.IsNullOrEmpty(sourceApp) ? "<unknown>" : sourceApp
    );

    // Both Tekla-sourced (TeklaObject) and Revit-sourced (BuiltElements.Beam/Column) objects
    // are handled natively by TeklaRootToHostConverter — use Native for all known sources.
    return ReceiveMode.Native;
  }
}
