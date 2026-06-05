using Microsoft.Extensions.Logging;
using Speckle.Connectors.DUI.Models.Card;
using Speckle.Connectors.DUI.Utils;
using Speckle.InterfaceGenerator;
using Speckle.Sdk;
using Speckle.Sdk.Common;
using Speckle.Sdk.Credentials;

namespace Speckle.Connectors.DUI.Models;

/// <summary>
/// Encapsulates the state Speckle needs to persist in the host app's document.
/// </summary>
[GenerateAutoInterface]
public abstract class DocumentModelStore(ILogger<DocumentModelStore> logger, IJsonSerializer serializer)
  : IDocumentModelStore
{
  public event EventHandler<ModelCardsChangedEventArgs>? ModelCardsChanged;
  private readonly List<ModelCard> _models = new();

  /// <summary>
  /// This event is triggered by each specific host app implementation of the document model store.
  /// </summary>
  // POC: unsure about the PublicAPI annotation, unsure if this changed handle should live here on the store...  :/
  public event EventHandler? DocumentChanged;

  //needed for javascript UI
  public IReadOnlyList<ModelCard> Models
  {
    get
    {
      lock (_models)
      {
        return _models.AsReadOnly();
      }
    }
  }

  protected void OnDocumentChanged() => DocumentChanged?.Invoke(this, EventArgs.Empty);

  public virtual Task OnDocumentStoreInitialized() => Task.CompletedTask;

  public virtual bool IsDocumentInit { get; set; }

  // TODO: not sure about this, throwing an exception, needs some thought...
  // Further note (dim): If we reach to the stage of throwing an exception here because a model is not found, there's a huge misalignment between the UI's list of model cards and the host app's.
  // In theory this should never really happen, (Adam) but it does because of threading so don't throw (as said above)
  public ModelCard? GetModelById(string id)
  {
    lock (_models)
    {
      var model = _models.FirstOrDefault(model => model.ModelCardId == id);
      if (model is null)
      {
        logger.LogWarning("Model with id {id} not found", id);
        return null;
      }
      return model;
    }
  }

  public void AddModel(ModelCard model)
  {
    lock (_models)
    {
      _models.Add(model);
      SaveState();
    }
  }

  public void ClearAndSave()
  {
    lock (_models)
    {
      _models.Clear();
      SaveState();
    }
  }

  public void UpdateModel(ModelCard model)
  {
    lock (_models)
    {
      var index = _models.FindIndex(m => m.ModelCardId == model.ModelCardId);
      if (index == -1)
      {
        logger.LogWarning("Model card not found to update. Model card ID: {ModelCardId}", model.ModelCardId);
        return;
      }
      _models[index] = model;
      SaveState();
    }
  }

  public void RemoveModel(ModelCard model)
  {
    lock (_models)
    {
      var index = _models.FindIndex(m => m.ModelCardId == model.ModelCardId);
      if (index == -1)
      {
        logger.LogWarning("Model card not found to update. Model card ID: {ModelCardId}", model.ModelCardId);
        return;
      }
      _models.RemoveAt(index);
      SaveState();
    }
  }

  public void RemoveModels(List<ModelCard> models)
  {
    lock (_models)
    {
      var listForMissingModelCards = new List<string>();
      foreach (var model in models)
      {
        var index = _models.FindIndex(m => m.ModelCardId == model.ModelCardId);
        if (index == -1)
        {
          listForMissingModelCards.Add(model.ModelCardId.NotNull());
        }
        _models.RemoveAt(index);
      }
      SaveState();
      if (listForMissingModelCards.Count > 0)
      {
        logger.LogWarning(
          "Model cards with IDs {ListForMissingModelCards} not found to remove",
          listForMissingModelCards
        );
      }
    }
  }

  public IEnumerable<SenderModelCard> GetSenders()
  {
    lock (_models)
    {
      return _models
        .Where(model => model.TypeDiscriminator == nameof(SenderModelCard))
        .Cast<SenderModelCard>()
        .ToList();
    }
  }

  /// <summary>
  /// Repairs model cards whose stored <see cref="ModelCard.AccountId"/> no longer matches any account in the
  /// local account manager — which happens when an account is removed and re-added (new local ID) or when the
  /// Speckle Manager regenerates account IDs.  Falls back to the first account with the same
  /// <see cref="ModelCard.ServerUrl"/> so the frontend can find the card without showing a false
  /// "project not accessible" error.
  /// </summary>
  protected void RepairStaleAccountIds(IAccountManager accountManager)
  {
    lock (_models)
    {
      var allAccounts = accountManager.GetAccounts().ToList();
      bool changed = false;

      foreach (var card in _models)
      {
        if (string.IsNullOrEmpty(card.AccountId) || string.IsNullOrEmpty(card.ServerUrl))
        {
          continue;
        }

        if (allAccounts.Any(a => a.id == card.AccountId))
        {
          continue;
        }

        // Stored AccountId is stale — find the best account for this server.
        // Capture ServerUrl in a local so the null-flow analysis is happy inside the lambda.
        string serverUrl = card.ServerUrl!;
        var fallback = allAccounts.FirstOrDefault(a =>
          a.serverInfo?.url is string url
          && string.Equals(url.TrimEnd('/'), serverUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
        );

        if (fallback is null)
        {
          continue;
        }

        logger.LogInformation(
          "Repaired stale AccountId {OldId} → {NewId} for model card {CardId} on {Server}",
          card.AccountId,
          fallback.id,
          card.ModelCardId,
          card.ServerUrl
        );
        card.AccountId = fallback.id;
        changed = true;
      }

      if (changed)
      {
        SaveState();
      }
    }
  }

  protected string Serialize() => serializer.Serialize(Models.ToList());

  // POC: this seemms more like a IModelsDeserializer?, seems disconnected from this class
  protected List<ModelCard> Deserialize(string models) => serializer.Deserialize<List<ModelCard>>(models).NotNull();

  protected void SaveState()
  {
    lock (_models)
    {
      var state = Serialize();
      HostAppSaveState(state);
      ModelCardsChanged?.Invoke(this, new ModelCardsChangedEventArgs(_models.ToList().AsReadOnly()));
    }
  }

  /// <summary>
  /// Implement this method according to the host app's specific ways of reading custom data from its file.
  /// </summary>
  protected abstract void HostAppSaveState(string modelCardState);

  protected abstract void LoadState();

  protected void LoadFromString(string? models)
  {
    try
    {
      lock (_models)
      {
        _models.Clear();
        if (string.IsNullOrEmpty(models))
        {
          return;
        }
        _models.AddRange(Deserialize(models.NotNull()).NotNull());
      }
    }
    catch (Exception ex) when (!ex.IsFatal())
    {
      ClearAndSave();
      logger.LogWarning(ex, "Failed to deserialize model cards from document");
    }
  }
}
