namespace Speckle.Converters.TeklaShared.Helpers;

/// <summary>
/// Standard Tekla class (color) assignments for parts created from received Revit elements.
/// Tekla-native receives are NOT covered here - they keep the class captured from the source
/// model (see TeklaPartPropertyApplicator). Material kind (steel vs concrete) is inferred from
/// the resolved Tekla material string, falling back to the Revit material name tokens.
/// </summary>
public static class TeklaStandardClasses
{
  public const string STEEL_BEAM = "3";
  public const string STEEL_COLUMN = "7";
  public const string STEEL_PLATE = "99";
  public const string CONCRETE_BEAM = "6";
  public const string CONCRETE_COLUMN = "13";
  public const string CONCRETE_PANEL = "1";
  public const string CONCRETE_SLAB = "11";
  public const string FOUNDATION = "8";

  /// <summary>
  /// Heuristic material-kind check on a Tekla material string (after mapping/validation), e.g.
  /// "C30/37"/"LC25/28" (EN concrete grades) or German/English material names containing
  /// "beton"/"concrete". Everything else (S235JR, S355J2, A36, ...) is treated as steel.
  /// </summary>
  public static bool IsConcreteMaterial(string material)
  {
    if (string.IsNullOrWhiteSpace(material))
    {
      return false;
    }

    string trimmed = material.Trim();
    if (StartsWithGradePrefix(trimmed, "C") || StartsWithGradePrefix(trimmed, "LC"))
    {
      return true;
    }

    return trimmed.IndexOf("beton", StringComparison.OrdinalIgnoreCase) >= 0
      || trimmed.IndexOf("concrete", StringComparison.OrdinalIgnoreCase) >= 0;
  }

  public static string ForBeam(string material) => IsConcreteMaterial(material) ? CONCRETE_BEAM : STEEL_BEAM;

  public static string ForColumn(string material) => IsConcreteMaterial(material) ? CONCRETE_COLUMN : STEEL_COLUMN;

  public static string ForSlab(string material) => IsConcreteMaterial(material) ? CONCRETE_SLAB : STEEL_PLATE;

  private static bool StartsWithGradePrefix(string value, string prefix) =>
    value.Length > prefix.Length
    && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
    && char.IsDigit(value[prefix.Length]);
}
