using Autodesk.Revit.DB;
using Microsoft.Extensions.Logging;

namespace Speckle.Connectors.Revit.Operations.Receive;

/// <summary>
/// This class will suppress warnings on the Revit UI, and attempt to auto-resolve any errors that
/// have a default resolution available (e.g. structural framing host/level mismatches raised
/// while creating native FamilyInstances). Currently used during Revit receive.
/// </summary>
public class HideWarningsFailuresPreprocessor(ILogger<HideWarningsFailuresPreprocessor> logger)
  : IFailuresPreprocessor,
    IFailureTracker
{
  private readonly List<string> _failureDescriptions = [];

  public IReadOnlyList<string> FailureDescriptions => _failureDescriptions;

  public void Reset() => _failureDescriptions.Clear();

  public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
  {
    bool resolvedAnyError = false;

    foreach (var failure in failuresAccessor.GetFailureMessages())
    {
      var severity = failure.GetSeverity();
      if (severity == FailureSeverity.Warning)
      {
        failuresAccessor.DeleteWarning(failure);
        continue;
      }

      bool hasResolutions = failure.HasResolutions();

      logger.LogWarning(
        "Revit failure during receive: {Description} (severity: {Severity}, hasResolutions: {HasResolutions})",
        failure.GetDescriptionText(),
        severity,
        hasResolutions
      );

      // Record every non-warning failure, even ones we attempt to resolve: HasResolutions() merely means a
      // default resolution exists, not that applying it will actually fix the problem - the transaction can
      // still roll back afterwards, and this is often the only clue as to why.
      _failureDescriptions.Add($"{failure.GetDescriptionText()} (severity: {severity})");

      if (hasResolutions)
      {
        failuresAccessor.ResolveFailure(failure);
        resolvedAnyError = true;
      }
    }

    return resolvedAnyError ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
  }
}
