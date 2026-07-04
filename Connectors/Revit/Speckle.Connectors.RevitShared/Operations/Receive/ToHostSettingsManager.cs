using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Extensions.Logging;
using Speckle.Connectors.DUI.Models.Card;
using Speckle.Converters.RevitShared.Helpers;
using Speckle.Converters.RevitShared.Settings;
using Speckle.InterfaceGenerator;
using ReceiverModelCard = Speckle.Connectors.DUI.Models.Card.ReceiverModelCard;

namespace Speckle.Connectors.Revit.Operations.Receive.Settings;

[GenerateAutoInterface]
public class ToHostSettingsManager : IToHostSettingsManager
{
  private readonly RevitContext _revitContext;
  private readonly ILogger<ToHostSettingsManager> _logger;

  public ToHostSettingsManager(RevitContext revitContext, ILogger<ToHostSettingsManager> logger)
  {
    _revitContext = revitContext;
    _logger = logger;
  }

  public Transform? GetReferencePointSetting(ModelCard modelCard)
  {
    var referencePointString =
      modelCard.Settings?.FirstOrDefault(s => s.Id == ReceiveReferencePointSetting.SETTING_ID)?.Value as string;
    if (
      referencePointString is not null
      && ReceiveReferencePointSetting.ReferencePointMap.TryGetValue(
        referencePointString,
        out ReceiveReferencePointType referencePoint
      )
    )
    {
      // get the current transform from setting first
      // we are doing this because we can't track if reference points were changed between send operations.
      Transform? currentTransform = GetTransform(referencePoint);
      return currentTransform;
    }

    // log the issue
    _logger.LogWarning(
      "Invalid reference point setting received: '{ReferencePointString}' for model {ModelCardId}, using default: {DefaultValue}",
      referencePointString,
      modelCard.ModelCardId,
      ReceiveReferencePointSetting.DEFAULT_VALUE
    );

    // return default (null for Source means no transform)
    return null;
  }

  /// <summary>
  /// Determines the <see cref="ReceiveMode"/> for a model card.
  /// When the user enables "Receive as Native Elements", the source application of the selected version
  /// is used to pick the right conversion strategy automatically:
  /// <list type="bullet">
  ///   <item>Tekla source → <see cref="ReceiveMode.NativeTekla"/> (structural framing via BeamToHostConverter)</item>
  ///   <item>Any other source (Revit, unknown) → <see cref="ReceiveMode.NativeRevit"/> (family-baking strategy)</item>
  /// </list>
  /// When native receive is disabled, always returns <see cref="ReceiveMode.DirectShape"/>.
  /// </summary>
  public ReceiveMode GetReceiveMode(ModelCard modelCard)
  {
    var nativeEnabled =
      modelCard.Settings?.FirstOrDefault(s => s.Id == ReceiveInstancesAsFamiliesSetting.SETTING_ID)?.Value as bool?
      ?? ReceiveInstancesAsFamiliesSetting.DEFAULT_VALUE;

    if (!nativeEnabled)
    {
      return ReceiveMode.DirectShape;
    }

    // Auto-detect from the source application of the version being received.
    string sourceApp = (modelCard as ReceiverModelCard)?.SelectedVersionSourceApp ?? string.Empty;

    _logger.LogInformation(
      "Native receive enabled for {ModelCardId}. Source application: '{SourceApp}'",
      modelCard.ModelCardId,
      string.IsNullOrEmpty(sourceApp) ? "<unknown>" : sourceApp
    );

    if (sourceApp.ContainsOrdinalIgnoreCase("tekla"))
    {
      return ReceiveMode.NativeTekla;
    }

    // Revit, grasshopper, or any unknown source — use the family-baking strategy.
    return ReceiveMode.NativeRevit;
  }

  private Transform? GetTransform(ReceiveReferencePointType referencePointType)
  {
    Transform? referencePointTransform = null;

    if (_revitContext.UIApplication is UIApplication uiApplication)
    {
      // first get the main doc base points
      using FilteredElementCollector filteredElementCollector = new(uiApplication.ActiveUIDocument.Document);
      var points = filteredElementCollector.OfClass(typeof(BasePoint)).Cast<BasePoint>().ToList();
      BasePoint? projectPoint = points.FirstOrDefault(o => !o.IsShared);
      BasePoint? surveyPoint = points.FirstOrDefault(o => o.IsShared);

      switch (referencePointType)
      {
        case ReceiveReferencePointType.ProjectBase:
          referencePointTransform = projectPoint is not null
            ? Transform.CreateTranslation(projectPoint.Position)
            : throw new InvalidOperationException("Couldn't retrieve Project Point from document");
          break;

        case ReceiveReferencePointType.Survey:
          referencePointTransform = surveyPoint is not null
            ? Transform.CreateTranslation(surveyPoint.Position)
            : throw new InvalidOperationException("Couldn't retrieve Survey Point from document");
          break;

        case ReceiveReferencePointType.SharedCoordinates:
          referencePointTransform =
            uiApplication.ActiveUIDocument.Document.ActiveProjectLocation?.GetTotalTransform()
            ?? throw new InvalidOperationException("Couldn't retrieve Shared Coordinates transform from document");
          break;

        case ReceiveReferencePointType.Source:
        case ReceiveReferencePointType.InternalOrigin:
          break;
      }

      return referencePointTransform;
    }

    throw new InvalidOperationException(
      "Revit Context UI Application was null when retrieving reference point transform"
    );
  }
}
