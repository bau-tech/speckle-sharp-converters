using Microsoft.Extensions.Logging;
using Speckle.Connectors.DUI.Models;
using Speckle.Connectors.DUI.Utils;
using Speckle.Sdk;
using Speckle.Sdk.Common;
using Speckle.Sdk.Credentials;
using Speckle.Sdk.SQLite;

namespace Speckle.Connectors.TeklaShared.HostApp;

public sealed class TeklaDocumentModelStore : DocumentModelStore, IDisposable
{
  private readonly ILogger<TeklaDocumentModelStore> _logger;
  private readonly ISqLiteJsonCacheManager _jsonCacheManager;
  private readonly TSM.Model _model;
  private string? _modelKey;
  private readonly TSM.Events _events;
  private readonly IAccountManager _accountManager;
  private readonly TSM.Events.ModelLoadDelegate _onModelLoad;

  public TeklaDocumentModelStore(
    ILogger<DocumentModelStore> baseLogger,
    IJsonSerializer jsonSerializer,
    ILogger<TeklaDocumentModelStore> logger,
    ISqLiteJsonCacheManagerFactory jsonCacheManagerFactory,
    IAccountManager accountManager
  )
    : base(baseLogger, jsonSerializer)
  {
    _logger = logger;
    _jsonCacheManager = jsonCacheManagerFactory.CreateForUser("ConnectorsFileData");
    _accountManager = accountManager;
    _events = new TSM.Events();
    _model = new TSM.Model();
    GenerateKey();
    _onModelLoad = () =>
    {
      GenerateKey();
      LoadState();
      OnDocumentChanged();
    };
    _events.ModelLoad += _onModelLoad;
    _events.Register();
    if (SpeckleTeklaPanelHost.IsInitialized)
    {
      LoadState();
      OnDocumentChanged();
    }
  }

  public void Dispose()
  {
    _events.ModelLoad -= _onModelLoad;
    _events.UnRegister();
    _jsonCacheManager.Dispose();
  }

  private void GenerateKey() => _modelKey = Md5.GetString(_model.GetInfo().ModelPath);

  protected override void HostAppSaveState(string modelCardState)
  {
    try
    {
      if (_modelKey is null)
      {
        return;
      }
      _jsonCacheManager.UpdateObject(_modelKey, modelCardState);
    }
    catch (Exception ex) when (!ex.IsFatal())
    {
      _logger.LogError(ex, "Failed to Save Host App State");
    }
  }

  protected override void LoadState()
  {
    if (_modelKey is null)
    {
      return;
    }
    var state = _jsonCacheManager.GetObject(_modelKey);
    LoadFromString(state);
    RepairStaleAccountIds(_accountManager);
  }
}
