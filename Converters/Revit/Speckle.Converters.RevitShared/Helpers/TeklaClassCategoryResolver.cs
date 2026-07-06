namespace Speckle.Converters.RevitShared.Helpers;

/// <summary>
/// Maps a Tekla part's captured Class property to the Revit BuiltInCategory it should become on
/// receive. Tekla's Open API has no distinct Column/Foundation type - vertical members and footings
/// are also TSM.Beam, so this Class number is the only signal distinguishing them. These mirror
/// TeklaStandardClasses in the Tekla converter project
/// (Converters/Tekla/Speckle.Converters.TeklaShared/Helpers/TeklaStandardClasses.cs) - duplicated
/// here rather than cross-referenced, since Revit and Tekla converters are separate assemblies with
/// no shared dependency between them. An unrecognized class falls back to OST_StructuralFraming -
/// safe/conservative, since a fabricator's custom class scheme is possible.
/// </summary>
public static class TeklaClassCategoryResolver
{
  private const string STEEL_COLUMN = "7";
  private const string CONCRETE_COLUMN = "13";
  private const string FOUNDATION = "8";
  private const string CONCRETE_PANEL = "1";
  private const string CONCRETE_SLAB = "11";

  public static DB.BuiltInCategory Resolve(string? teklaClass) =>
    teklaClass switch
    {
      STEEL_COLUMN or CONCRETE_COLUMN => DB.BuiltInCategory.OST_StructuralColumns,
      FOUNDATION => DB.BuiltInCategory.OST_StructuralFoundation,
      CONCRETE_PANEL => DB.BuiltInCategory.OST_Walls,
      CONCRETE_SLAB => DB.BuiltInCategory.OST_Floors,
      _ => DB.BuiltInCategory.OST_StructuralFraming,
    };
}
