namespace Speckle.Converters.RevitShared.Settings;

public enum ReceiveMode
{
  Native,
  DirectShape
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
