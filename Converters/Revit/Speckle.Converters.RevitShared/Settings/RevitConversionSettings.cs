namespace Speckle.Converters.RevitShared.Settings;

/// <summary>
/// Controls how incoming Speckle objects are materialised in Revit on receive.
/// The correct mode is auto-detected from <c>SelectedVersionSourceApp</c> on the model card
/// when the user enables native receive; it can also be set explicitly.
/// </summary>
public enum ReceiveMode
{
  /// <summary>All objects are received as DirectShape geometry (fastest, always works).</summary>
  DirectShape,

  /// <summary>Objects from a Revit model are received as native Revit families via the family-baking strategy.</summary>
  NativeRevit,

  /// <summary>Objects from a Tekla model are received as native Revit structural elements (beams, columns).</summary>
  NativeTekla,
}

public record RevitConversionSettings(
  DB.Document Document,
  DetailLevelType DetailLevel,
  DB.Transform? ReferencePointTransform,
  string SpeckleUnits,
  bool SendParameterNullOrEmptyStrings,
  bool SendLinkedModels,
  bool SendRebarsAsVolumetric,
  bool SendAreasAsMesh,
  ReceiveMode ReceiveMode = ReceiveMode.DirectShape,
  double Tolerance = 0.0164042 // 5mm in ft
);
