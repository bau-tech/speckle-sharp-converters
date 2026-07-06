namespace Speckle.Converters.RevitShared.Helpers.ProfileMapping;

/// <summary>
/// JSON-deserialized shape of the user-maintained mapping file at
/// <see cref="TeklaProfileMappingProvider.MappingFilePath"/>, keyed by "{category}|{profile}"
/// (falling back to a bare profile key) and pointing at the Revit "{family}|{type}" to use -
/// the mirror-image of Tekla's own RevitProfileMaterialMappingTable, for the opposite direction.
/// </summary>
public sealed class TeklaProfileMappingTable
{
  public int SchemaVersion { get; set; } = 1;
  public Dictionary<string, string> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
