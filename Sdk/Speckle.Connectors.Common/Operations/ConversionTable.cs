namespace Speckle.Connectors.Common.Operations;

/// <summary>
/// A distinct (category, family, type) entry the sender used, with optional
/// cross-section dimension hints in millimeters when they were cheaply derivable.
/// </summary>
public sealed class ConversionTableProfileEntry
{
  public string Category { get; set; } = "";

  /// <summary>
  /// Locale-independent category identifier (e.g. "OST_Walls"), used by the receiving connector to
  /// decide whether this entry's mapping is actually honored, without relying on the localized
  /// <see cref="Category"/> display name.
  /// </summary>
  public string BuiltInCategory { get; set; } = "";
  public string Family { get; set; } = "";
  public string Type { get; set; } = "";
  public double? WidthMm { get; set; }
  public double? HeightMm { get; set; }
}

/// <summary>A distinct structural material name the sender used.</summary>
public sealed class ConversionTableMaterialEntry
{
  public string Name { get; set; } = "";
}

/// <summary>
/// Direction-neutral inventory of the family/types and structural materials contained in a send,
/// attached to the root object under <see cref="RootKeys.CONVERSION_TABLE"/> so the receiving
/// connector can present a mapping UI before baking. Deliberately carries no target-app
/// suggestions: the sender does not know the receiver's catalogs.
/// Serialized as plain nested dictionaries/lists so no Base subclass or SDK change is needed.
/// </summary>
public sealed class ConversionTable
{
  private const int CURRENT_SCHEMA_VERSION = 1;

  public string SourceApplication { get; set; } = "";
  public List<ConversionTableProfileEntry> Profiles { get; } = new();
  public List<ConversionTableMaterialEntry> Materials { get; } = new();

  public Dictionary<string, object?> ToWire()
  {
    var profiles = new List<object>();
    foreach (var p in Profiles)
    {
      profiles.Add(
        new Dictionary<string, object?>
        {
          ["category"] = p.Category,
          ["builtInCategory"] = p.BuiltInCategory,
          ["family"] = p.Family,
          ["type"] = p.Type,
          ["widthMm"] = p.WidthMm,
          ["heightMm"] = p.HeightMm,
        }
      );
    }

    var materials = new List<object>();
    foreach (var m in Materials)
    {
      materials.Add(new Dictionary<string, object?> { ["name"] = m.Name });
    }

    return new Dictionary<string, object?>
    {
      ["schemaVersion"] = CURRENT_SCHEMA_VERSION,
      ["sourceApplication"] = SourceApplication,
      ["profiles"] = profiles,
      ["materials"] = materials,
    };
  }

  /// <summary>
  /// Tolerantly parses a deserialized wire value (nested dictionaries/lists, numbers possibly
  /// boxed as long or double, missing keys, foreign shapes). Never throws; returns false when
  /// the value carries no usable table.
  /// </summary>
  public static bool TryParse(object? wireValue, out ConversionTable table)
  {
    table = new ConversionTable();
    if (wireValue is not IReadOnlyDictionary<string, object?> dict)
    {
      // some deserializers hand back the mutable interface only
      if (wireValue is Dictionary<string, object?> mutable)
      {
        dict = mutable;
      }
      else
      {
        return false;
      }
    }

    table.SourceApplication = GetString(dict, "sourceApplication") ?? "";

    if (TryGetValue(dict, "profiles") is IEnumerable<object?> profiles)
    {
      foreach (var item in profiles)
      {
        if (item is not IReadOnlyDictionary<string, object?> entry)
        {
          continue;
        }
        string? type = GetString(entry, "type");
        if (string.IsNullOrWhiteSpace(type))
        {
          continue;
        }
        table.Profiles.Add(
          new ConversionTableProfileEntry
          {
            Category = GetString(entry, "category") ?? "",
            BuiltInCategory = GetString(entry, "builtInCategory") ?? "",
            Family = GetString(entry, "family") ?? "",
            Type = type!,
            WidthMm = GetDouble(entry, "widthMm"),
            HeightMm = GetDouble(entry, "heightMm"),
          }
        );
      }
    }

    if (TryGetValue(dict, "materials") is IEnumerable<object?> materials)
    {
      foreach (var item in materials)
      {
        if (item is not IReadOnlyDictionary<string, object?> entry)
        {
          continue;
        }
        string? name = GetString(entry, "name");
        if (!string.IsNullOrWhiteSpace(name))
        {
          table.Materials.Add(new ConversionTableMaterialEntry { Name = name! });
        }
      }
    }

    return table.Profiles.Count > 0 || table.Materials.Count > 0;
  }

  private static object? TryGetValue(IReadOnlyDictionary<string, object?> dict, string key) =>
    dict.TryGetValue(key, out object? value) ? value : null;

  private static string? GetString(IReadOnlyDictionary<string, object?> dict, string key) =>
    TryGetValue(dict, key) as string;

  private static double? GetDouble(IReadOnlyDictionary<string, object?> dict, string key)
  {
    object? value = TryGetValue(dict, key);
    if (value is null)
    {
      return null;
    }
    try
    {
      return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
    }
    catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
    {
      return null;
    }
  }
}
