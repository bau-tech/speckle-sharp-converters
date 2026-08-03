using Microsoft.Extensions.Logging;
using Speckle.Newtonsoft.Json;
using Speckle.Sdk.Logging;

namespace Speckle.Converters.TeklaShared.Helpers.ProfileMapping;

/// <summary>
/// Loads a user-maintained IFC-type-name → Tekla profile string mapping table from a hand-edited
/// JSON file, once per receive operation. Own file, own table - deliberately NOT merged into
/// <see cref="RevitProfileMaterialMappingProvider"/>'s table, since that one is keyed on a Revit
/// family/type pair and is a distinct source (the existing Revit→Tekla roundtrip), not IFC.
/// A missing file is the expected default state (no logging); a malformed file logs a warning and
/// is treated as empty - this table is a convenience layer for elements the IFC enricher couldn't
/// resolve a profile for at all (e.g. piles, non-rectangular/non-circular beams), never a reason to
/// block a receive.
/// </summary>
public class IfcProfileMappingProvider
{
  private readonly ILogger<IfcProfileMappingProvider> _logger;
  private readonly Lazy<Dictionary<string, string>> _table;
  private Dictionary<string, string>? _override;

  public IfcProfileMappingProvider(ILogger<IfcProfileMappingProvider> logger)
  {
    _logger = logger;
    _table = new Lazy<Dictionary<string, string>>(Load);
  }

  public static string MappingFilePath =>
    Path.Combine(SpecklePathProvider.UserSpeckleFolderPath, "Tekla", "ifc-profile-mapping.json");

  private Dictionary<string, string> EffectiveTable => _override ?? _table.Value;

  /// <summary>Looks up a Tekla profile string for an IFC element's <c>ifcTypeName</c> (ObjectType).</summary>
  public bool TryGetProfile(string ifcTypeName, out string? profile) =>
    EffectiveTable.TryGetValue(ifcTypeName, out profile);

  /// <summary>
  /// Replaces the effective table for the remainder of this scope (i.e. the current receive
  /// operation). Set by the receive-time mapping dialog; does not touch the file on disk.
  /// </summary>
  public void SetOverride(Dictionary<string, string> table) => _override = table;

  /// <summary>The table as loaded from disk (never null) - used to pre-fill the mapping dialog.</summary>
  public Dictionary<string, string> GetPersistedTable() => _table.Value;

  /// <summary>Persists a table to <see cref="MappingFilePath"/> ("save as default"). Non-fatal on failure.</summary>
  public bool TrySaveAsDefault(Dictionary<string, string> table)
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
      _logger.LogWarning(ex, "Failed to save the IFC profile mapping table to {Path}.", MappingFilePath);
      return false;
    }
  }

  private Dictionary<string, string> Load()
  {
    if (!File.Exists(MappingFilePath))
    {
      return new Dictionary<string, string>();
    }

    try
    {
      string json = File.ReadAllText(MappingFilePath);
      return JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
    }
    catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
    {
      _logger.LogWarning(
        ex,
        "Failed to load IFC profile mapping table at {Path}; proceeding with no mappings.",
        MappingFilePath
      );
      return new Dictionary<string, string>();
    }
  }
}
