using Speckle.Converters.TeklaShared.Helpers;

namespace Speckle.Converters.TeklaShared.Extensions;

public static class SpeckleApplicationIdExtensions
{
  // Round-tripped objects (originally authored in a DIFFERENT app, received into Tekla) re-emit the
  // ORIGIN applicationId stamped on them at receive time (see TeklaOriginIdentifier), so a downstream
  // app can recognize them as the same object on a later receive - mirrors the equivalent Revit-side
  // mechanism (OriginApplicationIdSchema / GetOutgoingApplicationId). Objects that have never
  // round-tripped (no stamp) fall back to Tekla's own native GUID, unchanged from before.
  public static string GetSpeckleApplicationId(this TSM.ModelObject modelObject) =>
    TeklaOriginIdentifier.TryGet(modelObject, out string? originApplicationId)
      ? originApplicationId
      : modelObject.Identifier.GUID.ToString();

  public static string GetSpeckleApplicationId(this TSMUI.Color color) =>
    $"color_{color.Red}_{color.Green}_{color.Blue}_{color.Transparency}";
}
