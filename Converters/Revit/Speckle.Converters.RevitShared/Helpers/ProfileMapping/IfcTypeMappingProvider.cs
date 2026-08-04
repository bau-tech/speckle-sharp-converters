using Microsoft.Extensions.Logging;
using Speckle.Newtonsoft.Json;
using Speckle.Sdk.Logging;

namespace Speckle.Converters.RevitShared.Helpers.ProfileMapping;

/// <summary>
/// Loads the user-maintained IFC-type-name → Revit-family/type mapping table from a hand-edited JSON
/// file, once per receive operation (this type is scoped, so it's naturally re-loaded on the next
/// receive - no explicit cache invalidation needed to pick up edits). A missing file is the expected
/// default state (no logging); a malformed file logs a warning and is treated as empty - this table is
/// a convenience layer, never a reason to block a receive.
/// </summary>
/// <remarks>
/// Deliberately a parallel, isolated file/class from <see cref="TeklaProfileMappingProvider"/> - own
/// JSON file, own persistence, no shared state - rather than generalizing the Tekla one, to guarantee
/// zero risk to the already-shipped Tekla↔Revit roundtrip (see the plan's "Isolation from the existing
/// Tekla↔Revit roundtrip" section for the reasoning this mirrors).
/// </remarks>
public class IfcTypeMappingProvider
{
  private readonly ILogger<IfcTypeMappingProvider> _logger;
  private readonly Lazy<IfcTypeMappingTable> _table;
  private IfcTypeMappingTable? _override;

  public IfcTypeMappingProvider(ILogger<IfcTypeMappingProvider> logger)
  {
    _logger = logger;
    _table = new Lazy<IfcTypeMappingTable>(Load);
  }

  public static string MappingFilePath =>
    Path.Combine(SpecklePathProvider.UserSpeckleFolderPath, "Revit", "ifc-type-mapping.json");

  private IfcTypeMappingTable EffectiveTable => _override ?? _table.Value;

  /// <summary>
  /// Replaces the effective table for the remainder of this scope (i.e. the current receive
  /// operation). Set by the receive-time mapping dialog; does not touch the file on disk.
  /// </summary>
  public void SetOverride(IfcTypeMappingTable table) => _override = table;

  /// <summary>The table as loaded from disk (never null) - used to pre-fill the mapping dialog.</summary>
  public IfcTypeMappingTable PersistedTable => _table.Value;

  /// <summary>Persists a table to <see cref="MappingFilePath"/> ("save as default"). Non-fatal on failure.</summary>
  public bool TrySaveAsDefault(IfcTypeMappingTable table)
  {
    try
    {
      string? directory = Path.GetDirectoryName(MappingFilePath);
      if (!string.IsNullOrEmpty(directory))
      {
        Directory.CreateDirectory(directory);
      }
      File.WriteAllText(MappingFilePath, JsonConvert.SerializeObject(table, Formatting.Indented));
      return true;
    }
    catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
    {
      _logger.LogWarning(ex, "Failed to save the IFC type mapping table to {Path}.", MappingFilePath);
      return false;
    }
  }

  /// <summary>
  /// Looks up the Revit family/type mapped for an IFC type name (e.g. an <c>IfcBeam</c>'s
  /// <c>ObjectType</c> string, such as <c>"M_Concrete-Rectangular Beam:400 x 800mm"</c>) within a
  /// category, trying the composite "{category}|{typeName}" key first, then falling back to a bare
  /// "{typeName}" key.
  /// </summary>
  public bool TryGetFamilyType(DB.BuiltInCategory category, string typeName, out string? family, out string? type)
  {
    family = null;
    type = null;

    var types = EffectiveTable.Types;
    if (!types.TryGetValue($"{category}|{typeName}", out string? mapped) && !types.TryGetValue(typeName, out mapped))
    {
      return false;
    }

    string[] parts = (mapped ?? "").Split('|');
    if (parts.Length != 2)
    {
      return false;
    }

    family = parts[0];
    type = parts[1];
    return true;
  }

  private IfcTypeMappingTable Load()
  {
    if (!File.Exists(MappingFilePath))
    {
      return new IfcTypeMappingTable();
    }

    try
    {
      string json = File.ReadAllText(MappingFilePath);
      return JsonConvert.DeserializeObject<IfcTypeMappingTable>(json) ?? new IfcTypeMappingTable();
    }
    catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
    {
      _logger.LogWarning(
        ex,
        "Failed to load IFC type mapping table at {Path}; proceeding with no mappings.",
        MappingFilePath
      );
      return new IfcTypeMappingTable();
    }
  }
}
