namespace Speckle.Connectors.Revit.Operations.Receive;

/// <summary>
/// Tracks non-warning Revit failure messages seen by <see cref="HideWarningsFailuresPreprocessor"/> during a
/// transaction, so callers (e.g. <see cref="ITransactionManager"/>) can surface the underlying cause when a
/// transaction commit unexpectedly rolls back - even if the failure appeared resolvable at the time.
/// </summary>
public interface IFailureTracker
{
  IReadOnlyList<string> FailureDescriptions { get; }

  /// <summary>Clears any failure descriptions recorded by a previous transaction.</summary>
  void Reset();
}
