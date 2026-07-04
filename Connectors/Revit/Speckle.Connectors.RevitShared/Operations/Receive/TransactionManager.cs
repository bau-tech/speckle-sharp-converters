using Autodesk.Revit.DB;
using Speckle.Converters.Common;
using Speckle.Converters.RevitShared.Settings;
using Speckle.Sdk;

namespace Speckle.Connectors.Revit.Operations.Receive;

/// <summary>
/// Is responsible for all functionality regarding subtransactions, transactions, and transaction groups.
/// This includes starting, pausing, committing, and rolling back transactions
/// </summary>
public sealed class TransactionManager : ITransactionManager
{
  private readonly IConverterSettingsStore<RevitConversionSettings> _converterSettings;
  private readonly IFailuresPreprocessor _errorPreprocessingService;
  private readonly IFailureTracker _failureTracker;
  private Document Document => _converterSettings.Current.Document;

  public TransactionManager(
    IConverterSettingsStore<RevitConversionSettings> converterSettings,
    IFailuresPreprocessor errorPreprocessingService,
    IFailureTracker failureTracker
  )
  {
    _converterSettings = converterSettings;
    _errorPreprocessingService = errorPreprocessingService;
    _failureTracker = failureTracker;
  }

  // poc : these are being disposed. I'm not sure why I need to supress this warning
#pragma warning disable CA2213 // Disposable fields should be disposed
  private Transaction? _transaction;
  private SubTransaction? _subTransaction;
#pragma warning restore CA2213 // Disposable fields should be disposed

  // POC find a better way to use IFailuresPreprocessor
  public void StartTransaction(bool enableFailurePreprocessor = false, string name = "Speckle Transaction")
  {
    if (_transaction == null || !_transaction.IsValidObject || _transaction.GetStatus() != TransactionStatus.Started)
    {
      _transaction = new Transaction(Document, name);

      if (enableFailurePreprocessor)
      {
        var failOpts = _transaction.GetFailureHandlingOptions();
        failOpts.SetFailuresPreprocessor(_errorPreprocessingService);
        failOpts.SetClearAfterRollback(true);
        _transaction.SetFailureHandlingOptions(failOpts);
        _failureTracker.Reset();
      }

      _transaction.Start();
    }
  }

  public TransactionStatus CommitTransaction()
  {
    if (
      _subTransaction != null
      && _subTransaction.IsValidObject
      && _subTransaction.GetStatus() == TransactionStatus.Started
    )
    {
      var subStatus = _subTransaction.Commit();
      if (subStatus != TransactionStatus.Committed)
      {
        _transaction?.RollBack();
        throw new SpeckleException($"Revit sub-transaction could not be committed (status: {subStatus}).");
      }
    }
    if (_transaction != null && _transaction.IsValidObject && _transaction.GetStatus() == TransactionStatus.Started)
    {
      var status = _transaction.Commit();
      if (status != TransactionStatus.Committed)
      {
        string message = $"Revit transaction could not be committed (status: {status}).";
        message += _failureTracker.FailureDescriptions.Count > 0
          ? $" Revit failures seen during commit: {string.Join("; ", _failureTracker.FailureDescriptions)}"
          : " No Revit failure messages were reported during commit.";

        throw new SpeckleException(message);
      }
      return status;
    }
    return TransactionStatus.Uninitialized;
  }

  public void RollbackTransaction()
  {
    RollbackSubTransaction();
    if (_transaction != null && _transaction.IsValidObject && _transaction.GetStatus() == TransactionStatus.Started)
    {
      _transaction.RollBack();
    }
  }

  public void StartSubtransaction()
  {
    StartTransaction();
    if (
      _subTransaction == null
      || !_subTransaction.IsValidObject
      || _subTransaction.GetStatus() != TransactionStatus.Started
    )
    {
      _subTransaction = new SubTransaction(Document);
      _subTransaction.Start();
    }
  }

  public TransactionStatus CommitSubtransaction()
  {
    if (_subTransaction != null && _subTransaction.IsValidObject)
    {
      var status = _subTransaction.Commit();
      if (status != TransactionStatus.Committed)
      {
        _transaction?.RollBack();
        throw new SpeckleException($"Revit sub-transaction could not be committed (status: {status}).");
      }
      return status;
    }
    return TransactionStatus.Uninitialized;
  }

  public void RollbackSubTransaction()
  {
    if (
      _subTransaction != null
      && _subTransaction.IsValidObject
      && _subTransaction.GetStatus() == TransactionStatus.Started
    )
    {
      _subTransaction.RollBack();
    }
  }

  public void Dispose()
  {
    _subTransaction?.Dispose();
    _transaction?.Dispose();
  }
}
