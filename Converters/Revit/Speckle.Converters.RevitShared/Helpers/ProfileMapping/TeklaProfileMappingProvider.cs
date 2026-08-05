using Microsoft.Extensions.Logging;
using Speckle.Newtonsoft.Json;
using Speckle.Sdk.Logging;

namespace Speckle.Converters.RevitShared.Helpers.ProfileMapping;

/// <summary>
/// Loads the user-maintained Tekla-profile → Revit-family/type mapping table from a hand-edited
/// JSON file, once per receive operation (this type is scoped, so it's naturally re-loaded on the
/// next receive - no explicit cache invalidation needed to pick up edits). A missing file is the
/// expected default state (no logging); a malformed file logs a warning and is treated as empty -
/// this table is a convenience layer, never a reason to block a receive.
/// </summary>
public class TeklaProfileMappingProvider
{
  private readonly ILogger<TeklaProfileMappingProvider> _logger;
  private readonly Lazy<TeklaProfileMappingTable> _table;
  private TeklaProfileMappingTable? _override;

  public TeklaProfileMappingProvider(ILogger<TeklaProfileMappingProvider> logger)
  {
    _logger = logger;
    _table = new Lazy<TeklaProfileMappingTable>(Load);
  }

  public static string MappingFilePath =>
    Path.Combine(SpecklePathProvider.UserSpeckleFolderPath, "Revit", "tekla-profile-mapping.json");

  private TeklaProfileMappingTable EffectiveTable => _override ?? _table.Value;

  /// <summary>
  /// Replaces the effective table for the remainder of this scope (i.e. the current receive
  /// operation). Set by the receive-time mapping dialog; does not touch the file on disk.
  /// </summary>
  public void SetOverride(TeklaProfileMappingTable table) => _override = table;

  /// <summary>The table as loaded from disk (never null) - used to pre-fill the mapping dialog.</summary>
  public TeklaProfileMappingTable PersistedTable => _table.Value;

  /// <summary>Persists a table to <see cref="MappingFilePath"/> ("save as default"). Non-fatal on failure.</summary>
  public bool TrySaveAsDefault(TeklaProfileMappingTable table)
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
      _logger.LogWarning(ex, "Failed to save the Tekla profile mapping table to {Path}.", MappingFilePath);
      return false;
    }
  }

  /// <summary>
  /// Looks up the Revit family/type mapped for a Tekla profile string within a category, trying the
  /// composite "{category}|{profile}" key first, then falling back to a bare "{profile}" key.
  /// </summary>
  public bool TryGetFamilyType(DB.BuiltInCategory category, string profile, out string? family, out string? type)
  {
    family = null;
    type = null;

    var profiles = EffectiveTable.Profiles;
    if (
      !profiles.TryGetValue($"{category}|{profile}", out string? mapped) && !profiles.TryGetValue(profile, out mapped)
    )
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

  private TeklaProfileMappingTable Load()
  {
    if (!File.Exists(MappingFilePath))
    {
      return new TeklaProfileMappingTable();
    }

    try
    {
      string json = File.ReadAllText(MappingFilePath);
      return JsonConvert.DeserializeObject<TeklaProfileMappingTable>(json) ?? new TeklaProfileMappingTable();
    }
    catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
    {
      _logger.LogWarning(
        ex,
        "Failed to load Tekla profile mapping table at {Path}; proceeding with no mappings.",
        MappingFilePath
      );
      return new TeklaProfileMappingTable();
    }
  }
}
