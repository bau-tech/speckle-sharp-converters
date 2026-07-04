namespace Speckle.Converters.TeklaShared.Helpers.ProfileMapping;

/// <summary>
/// JSON-deserialized shape of the user-maintained mapping file at
/// <see cref="RevitProfileMaterialMappingProvider.MappingFilePath"/>, keyed by Revit family/type
/// name (profiles) or Revit structural material name (materials) and pointing at the literal
/// Tekla catalog string to use.
/// </summary>
public sealed class RevitProfileMaterialMappingTable
{
  public int SchemaVersion { get; set; } = 1;
  public Dictionary<string, string> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
  public Dictionary<string, string> Materials { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
