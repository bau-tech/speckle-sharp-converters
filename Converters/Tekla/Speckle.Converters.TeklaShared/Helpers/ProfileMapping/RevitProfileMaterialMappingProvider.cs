using Microsoft.Extensions.Logging;
using Speckle.Newtonsoft.Json;
using Speckle.Sdk.Logging;

namespace Speckle.Converters.TeklaShared.Helpers.ProfileMapping;

/// <summary>
/// Loads the user-maintained Revit-family/type → Tekla profile/material mapping table from a
/// hand-edited JSON file, once per receive operation (this type is scoped, so it's naturally
/// re-loaded on the next receive - no explicit cache invalidation needed to pick up edits).
/// A missing file is the expected default state (no logging); a malformed file logs a warning
/// and is treated as empty - this table is a convenience layer, never a reason to block a receive.
/// </summary>
public class RevitProfileMaterialMappingProvider
{
  private readonly ILogger<RevitProfileMaterialMappingProvider> _logger;
  private readonly Lazy<RevitProfileMaterialMappingTable> _table;
  private RevitProfileMaterialMappingTable? _override;

  public RevitProfileMaterialMappingProvider(ILogger<RevitProfileMaterialMappingProvider> logger)
  {
    _logger = logger;
    _table = new Lazy<RevitProfileMaterialMappingTable>(Load);
  }

  public static string MappingFilePath =>
    Path.Combine(SpecklePathProvider.UserSpeckleFolderPath, "Tekla", "revit-profile-material-mapping.json");

  private RevitProfileMaterialMappingTable EffectiveTable => _override ?? _table.Value;

  /// <summary>
  /// Replaces the effective table for the remainder of this scope (i.e. the current receive
  /// operation). Set by the receive-time mapping dialog; does not touch the file on disk.
  /// </summary>
  public void SetOverride(RevitProfileMaterialMappingTable table) => _override = table;

  /// <summary>The table as loaded from disk (never null) - used to pre-fill the mapping dialog.</summary>
  public RevitProfileMaterialMappingTable GetPersistedTable() => _table.Value;

  /// <summary>Persists a table to <see cref="MappingFilePath"/> ("save as default"). Non-fatal on failure.</summary>
  public bool TrySaveAsDefault(RevitProfileMaterialMappingTable table)
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
      _logger.LogWarning(ex, "Failed to save the mapping table to {Path}.", MappingFilePath);
      return false;
    }
  }

  /// <summary>
  /// Looks up a Tekla profile string for a Revit family/type, trying the composite
  /// "{family}|{type}" key first, then falling back to a bare "{type}" key.
  /// </summary>
  public bool TryGetProfile(string family, string type, out string? profile)
  {
    var profiles = EffectiveTable.Profiles;
    return profiles.TryGetValue($"{family}|{type}", out profile) || profiles.TryGetValue(type, out profile);
  }

  /// <summary>Looks up a Tekla material string for a captured Revit structural material name.</summary>
  public bool TryGetMaterial(string revitMaterialName, out string? material) =>
    EffectiveTable.Materials.TryGetValue(revitMaterialName, out material);

  private RevitProfileMaterialMappingTable Load()
  {
    if (!File.Exists(MappingFilePath))
    {
      return new RevitProfileMaterialMappingTable();
    }

    try
    {
      string json = File.ReadAllText(MappingFilePath);
      return JsonConvert.DeserializeObject<RevitProfileMaterialMappingTable>(json)
        ?? new RevitProfileMaterialMappingTable();
    }
    catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
    {
      _logger.LogWarning(
        ex,
        "Failed to load Revit profile/material mapping table at {Path}; proceeding with no mappings.",
        MappingFilePath
      );
      return new RevitProfileMaterialMappingTable();
    }
  }
}
