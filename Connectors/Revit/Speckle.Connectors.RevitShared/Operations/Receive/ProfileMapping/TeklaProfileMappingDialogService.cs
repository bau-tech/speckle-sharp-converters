using Microsoft.Extensions.Logging;
using Speckle.Converters.RevitShared.Helpers;
using Speckle.Converters.RevitShared.Helpers.ProfileMapping;
using Speckle.Converters.RevitShared.ToHost;
using Speckle.Objects.Data;
using DB = Autodesk.Revit.DB;

namespace Speckle.Connectors.Revit.Operations.Receive.ProfileMapping;

/// <summary>Outcome of a confirmed mapping dialog; null result from ShowDialog means the user cancelled.</summary>
public sealed class ProfileMappingResult
{
  public ProfileMappingResult(TeklaProfileMappingTable table, bool saveAsDefault)
  {
    Table = table;
    SaveAsDefault = saveAsDefault;
  }

  public TeklaProfileMappingTable Table { get; }
  public bool SaveAsDefault { get; }
}

/// <summary>
/// Builds the rows for and shows the receive-time Tekla-profile-mapping dialog: one row per
/// distinct (category, profile) pair that can't already auto-resolve (rectangular profiles are
/// synthesized on the fly and never need a row), pre-filled from the persisted JSON mapping.
/// Mirrors the Tekla-side ConversionMappingDialogService, for the opposite receive direction.
/// </summary>
public sealed class TeklaProfileMappingDialogService
{
  private readonly TeklaProfileMappingProvider _mappingProvider;
  private readonly RevitElementTypeResolver _typeResolver;
  private readonly ILogger<TeklaProfileMappingDialogService> _logger;

  public TeklaProfileMappingDialogService(
    TeklaProfileMappingProvider mappingProvider,
    RevitElementTypeResolver typeResolver,
    ILogger<TeklaProfileMappingDialogService> logger
  )
  {
    _mappingProvider = mappingProvider;
    _typeResolver = typeResolver;
    _logger = logger;
  }

  public List<ProfileMappingRow> BuildRows(IReadOnlyList<TeklaObject> teklaObjects)
  {
    var rows = new List<ProfileMappingRow>();
    var persisted = _mappingProvider.PersistedTable;
    var seen = new HashSet<(DB.BuiltInCategory Category, string Profile)>();

    foreach (var teklaObject in teklaObjects)
    {
      // "Beam" is the only TeklaObject.type that carries a profile today (see
      // TeklaClassCategoryResolver) - Beams, Columns, and point/line Foundations all arrive as this.
      if (teklaObject.type != "Beam")
      {
        continue;
      }

      string? profile = teklaObject.properties.GetOrDefault("profile") as string;
      if (string.IsNullOrWhiteSpace(profile))
      {
        continue;
      }

      string? teklaClass = teklaObject.properties.GetOrDefault("class") as string;
      DB.BuiltInCategory category = TeklaClassCategoryResolver.Resolve(teklaClass);

      // Rectangular profiles auto-resolve for Beams/Columns (synthesized on the fly - see
      // StructuralFramingHelper.ResolveSymbol) so they don't need a row. Foundations are different:
      // a footing's "{width}*{depth}" profile is its PLAN FOOTPRINT, not a cross-section, and there's
      // no reliable universal parameter-name heuristic to auto-size/pick a footing type (family
      // authors name width/length/thickness params however they like) - always offer a row so the
      // user can pick the exact existing type instead of us guessing at it.
      if (
        category != DB.BuiltInCategory.OST_StructuralFoundation
        && StructuralFramingHelper.TryParseRectangularProfileMm(profile, out _, out _)
      )
      {
        continue;
      }

      if (!seen.Add((category, profile!)))
      {
        continue;
      }

      // Already resolved via a previously saved default - no need to re-prompt for it.
      if (persisted.Profiles.ContainsKey($"{category}|{profile}") || persisted.Profiles.ContainsKey(profile!))
      {
        continue;
      }

      var options = _typeResolver
        .GetFamilySymbolOptions(category)
        .Select(o => new FamilySymbolOption(o.Family, o.Type))
        .ToList();

      _logger.LogInformation(
        "TeklaProfileMappingDialogService.BuildRows: row category={Category} profile={Profile} optionCount={OptionCount}",
        category,
        profile,
        options.Count
      );

      rows.Add(
        new ProfileMappingRow
        {
          Category = category,
          Profile = profile!,
          DisplaySource = $"{profile}  ({CategoryDisplayName(category)})",
          Options = options,
        }
      );
    }

    _logger.LogInformation("TeklaProfileMappingDialogService.BuildRows: {Count} row(s) built.", rows.Count);
    return rows;
  }

  /// <summary>Shows the modal dialog. MUST be called on Revit's main/API thread.</summary>
  public ProfileMappingResult? ShowDialog(List<ProfileMappingRow> rows)
  {
    var dialog = new TeklaProfileMappingDialog(rows);
    bool? confirmed = dialog.ShowDialog();
    if (confirmed != true)
    {
      _logger.LogInformation("TeklaProfileMappingDialogService.ShowDialog: cancelled by the user.");
      return null;
    }

    foreach (var row in rows)
    {
      _logger.LogInformation(
        "TeklaProfileMappingDialogService.ShowDialog: row category={Category} profile={Profile} selected={Selected}",
        row.Category,
        row.Profile,
        row.Selected?.DisplayName ?? "(none)"
      );
    }

    var table = BuildEditedTable(rows);
    _logger.LogInformation(
      "TeklaProfileMappingDialogService.ShowDialog: confirmed, saveAsDefault={SaveAsDefault}, edited table has {Count} profile entr(y/ies): {Entries}",
      dialog.SaveAsDefault,
      table.Profiles.Count,
      string.Join(", ", table.Profiles.Select(p => $"{p.Key}={p.Value}"))
    );

    return new ProfileMappingResult(table, dialog.SaveAsDefault);
  }

  /// <summary>
  /// Starts from the persisted table (so unrelated hand-added entries survive a "save as default")
  /// and overlays the dialog rows: a selected option sets the composite-key entry, an unselected row
  /// removes it.
  /// </summary>
  private TeklaProfileMappingTable BuildEditedTable(List<ProfileMappingRow> rows)
  {
    var persisted = _mappingProvider.PersistedTable;
    var edited = new TeklaProfileMappingTable();
    foreach (var pair in persisted.Profiles)
    {
      edited.Profiles[pair.Key] = pair.Value;
    }

    foreach (var row in rows)
    {
      string key = $"{row.Category}|{row.Profile}";
      if (row.Selected is { } selected)
      {
        edited.Profiles[key] = $"{selected.Family}|{selected.Type}";
      }
      else
      {
        edited.Profiles.Remove(key);
      }
    }

    return edited;
  }

  private static string CategoryDisplayName(DB.BuiltInCategory category) =>
    category switch
    {
      DB.BuiltInCategory.OST_StructuralColumns => "Column",
      DB.BuiltInCategory.OST_StructuralFoundation => "Foundation",
      _ => "Structural Framing",
    };
}
