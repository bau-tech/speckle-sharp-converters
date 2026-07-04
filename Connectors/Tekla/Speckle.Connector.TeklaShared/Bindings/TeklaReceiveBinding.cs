using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Speckle.Connectors.Common.Cancellation;
using Speckle.Connectors.DUI.Bindings;
using Speckle.Connectors.DUI.Bridge;
using Speckle.Connectors.DUI.Settings;
using Speckle.Connectors.TeklaShared.Operations.Receive.Settings;
using Speckle.Converters.Common;
using Speckle.Converters.TeklaShared;

namespace Speckle.Connectors.TeklaShared.Bindings;

public sealed class TeklaReceiveBinding(
  ICancellationManager cancellationManager,
  IBrowserBridge parent,
  ILogger<TeklaReceiveBinding> logger,
  IReceiveOperationManagerFactory receiveOperationManagerFactory,
  ITeklaToHostSettingsManager toHostSettingsManager
) : IReceiveBinding
{
  public string Name => "receiveBinding";
  public IBrowserBridge Parent { get; } = parent;
  private IReceiveBindingUICommands Commands { get; } = new ReceiveBindingUICommands(parent);

#pragma warning disable CA1024
  public List<ICardSetting> GetReceiveSettings() => [new ReceiveModeSetting()];
#pragma warning restore CA1024

  public void CancelReceive(string modelCardId) => cancellationManager.CancelOperation(modelCardId);

  public async Task Receive(string modelCardId)
  {
    using var manager = receiveOperationManagerFactory.Create();
    await manager.Process(
      Commands,
      modelCardId,
      (sp, card) =>
      {
        var converterSettingsStore = sp.GetRequiredService<IConverterSettingsStore<TeklaConversionSettings>>();
        var teklaConversionSettingsFactory = sp.GetRequiredService<ITeklaConversionSettingsFactory>();

        // Initialize settings
        converterSettingsStore.Initialize(
          teklaConversionSettingsFactory.Create(
            sp.GetRequiredService<Tekla.Structures.Model.Model>(),
            false, // TODO: re-calculate sendrebarsassolid maybe?
            toHostSettingsManager.GetReceiveModeSetting(card)
          )
        );
      },
      async (_, processor) =>
      {
        try
        {
          return await processor();
        }
#pragma warning disable CA1031
        // OperationCanceledException must reach ReceiveOperationManager, which handles
        // user-cancels (UI cancel button or the conversion mapping dialog) silently.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
          logger.LogError(ex, "Failed to receive in Tekla");
          // TODO: handle UI reporting
          return null;
        }
      }
    );
  }
}
