namespace Speckle.Converters.RevitShared.Helpers.ProfileMapping;

/// <summary>
/// JSON-deserialized shape of the user-maintained mapping file at
/// <see cref="IfcTypeMappingProvider.MappingFilePath"/>, keyed by "{category}|{ifcTypeName}"
/// (falling back to a bare type-name key) and pointing at the Revit "{family}|{type}" to use.
/// Mirrors <see cref="TeklaProfileMappingTable"/>, but keyed on the IFC element's <c>ObjectType</c>/
/// type name string rather than a parsed profile - IFC-origin elements (e.g. beams with an
/// <c>AdvancedBrep</c> body) frequently have no structured profile to parse at all, so this is the
/// primary resolution path for them, not a fallback.
/// </summary>
public sealed class IfcTypeMappingTable
{
  public int SchemaVersion { get; set; } = 1;
  public Dictionary<string, string> Types { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
