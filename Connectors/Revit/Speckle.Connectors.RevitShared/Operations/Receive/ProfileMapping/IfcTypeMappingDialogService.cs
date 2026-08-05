using Microsoft.Extensions.Logging;
using Speckle.Converters.RevitShared.Helpers;
using Speckle.Converters.RevitShared.Helpers.ProfileMapping;
using Speckle.Converters.RevitShared.ToHost;
using Speckle.Objects.Data;
using DB = Autodesk.Revit.DB;

namespace Speckle.Connectors.Revit.Operations.Receive.ProfileMapping;

/// <summary>Outcome of a confirmed mapping dialog; null result from ShowDialog means the user cancelled.</summary>
public sealed class IfcTypeMappingResult
{
  public IfcTypeMappingResult(IfcTypeMappingTable table, bool saveAsDefault)
  {
    Table = table;
    SaveAsDefault = saveAsDefault;
  }

  public IfcTypeMappingTable Table { get; }
  public bool SaveAsDefault { get; }
}

/// <summary>
/// Builds the rows for and shows the receive-time IFC-type-mapping dialog: one row per distinct
/// (category, ifcTypeName) pair that can't already auto-resolve (a profile that already parses as a
/// rectangular or circular cross-section never needs a row - see <c>RevitNativeSchemaEnricher</c>'s
/// beam/column enrichment, which sets a profile whenever extraction succeeds), pre-filled from the
/// persisted JSON mapping. Deliberately a separate,
/// isolated service from <see cref="TeklaProfileMappingDialogService"/> - see
/// <see cref="IfcTypeMappingProvider"/>'s remarks for why - but reuses the generic
/// <see cref="ProfileMappingRow"/>/<see cref="FamilySymbolOption"/> view-model types, which carry no
/// Tekla-specific state.
/// </summary>
public sealed class IfcTypeMappingDialogService
{
  private readonly IfcTypeMappingProvider _mappingProvider;
  private readonly RevitElementTypeResolver _typeResolver;
  private readonly ILogger<IfcTypeMappingDialogService> _logger;

  public IfcTypeMappingDialogService(
    IfcTypeMappingProvider mappingProvider,
    RevitElementTypeResolver typeResolver,
    ILogger<IfcTypeMappingDialogService> logger
  )
  {
    _mappingProvider = mappingProvider;
    _typeResolver = typeResolver;
    _logger = logger;
  }

  public List<ProfileMappingRow> BuildRows(IReadOnlyList<DataObject> ifcObjects)
  {
    var rows = new List<ProfileMappingRow>();
    var persisted = _mappingProvider.PersistedTable;
    var seen = new HashSet<(DB.BuiltInCategory Category, string IfcTypeName)>();

    foreach (var dataObject in ifcObjects)
    {
      // Prototype scope: only categories RevitNativeSchemaEnricher actually enriches today.
      // OST_StructuralFoundation added for piles (AdvancedBrep Body, no profile at all - see
      // RevitNativeSchemaEnricher.TryEnrichPileFromPlacement) - the mapping dialog is their ONLY
      // resolution path, since there's no dimension data to auto-resolve from. Pad footings/pile caps
      // never reach here at all (TryEnrichRectangularPadFootingFromBody/TryEnrichFooting deliberately
      // never set ifcTypeName - see those methods' remarks - so they're filtered out by the
      // ifcTypeName check below and keep resolving via FoundationToHostConverter's own
      // swap-or-first-available logic, unaffected by this dialog).
      if (
        dataObject["builtInCategory"] is not string categoryStr
        || !Enum.TryParse(categoryStr, out DB.BuiltInCategory category)
        || (
          category != DB.BuiltInCategory.OST_StructuralFraming
          && category != DB.BuiltInCategory.OST_StructuralColumns
          && category != DB.BuiltInCategory.OST_StructuralFoundation
        )
      )
      {
        continue;
      }

      if (dataObject["ifcTypeName"] is not string ifcTypeName || string.IsNullOrWhiteSpace(ifcTypeName))
      {
        continue;
      }

      // A profile that already parses (rectangular or circular) only auto-resolves via
      // StructuralFramingHelper.ResolveSymbol's dimension-based tiers if a matching/duplicatable
      // symbol actually exists in THIS document (RevitElementTypeResolver.FindOrCreateXSymbol needs an
      // existing symbol exposing the right named parameter to match or duplicate from - see
      // HasRectangularSymbolTemplate/HasCircularSymbolTemplate's remarks). A profile string parsing
      // successfully is not the same guarantee - a document with no round column family loaded at all
      // would otherwise never offer a row for a perfectly-extracted circular column, silently falling
      // to DirectShape with no way to fix it (found from a live receive, not a hypothetical).
      string? profile = dataObject["profile"] as string;
      bool autoResolves =
        (
          StructuralFramingHelper.TryParseRectangularProfileMm(profile, out _, out _)
          && _typeResolver.HasRectangularSymbolTemplate(category)
        )
        || (
          StructuralFramingHelper.TryParseCircularProfileMm(profile, out _)
          && _typeResolver.HasCircularSymbolTemplate(category)
        );
      if (autoResolves)
      {
        continue;
      }

      if (!seen.Add((category, ifcTypeName)))
      {
        continue;
      }

      // Already resolved via a previously saved default - no need to re-prompt for it.
      if (persisted.Types.ContainsKey($"{category}|{ifcTypeName}") || persisted.Types.ContainsKey(ifcTypeName))
      {
        continue;
      }

      var options = _typeResolver
        .GetFamilySymbolOptions(category)
        .Select(o => new FamilySymbolOption(o.Family, o.Type))
        .ToList();

      _logger.LogInformation(
        "IfcTypeMappingDialogService.BuildRows: row category={Category} ifcTypeName={IfcTypeName} optionCount={OptionCount}",
        category,
        ifcTypeName,
        options.Count
      );

      rows.Add(
        new ProfileMappingRow
        {
          Category = category,
          Profile = ifcTypeName,
          DisplaySource = $"{ifcTypeName}  ({CategoryDisplayName(category)})",
          Options = options,
        }
      );
    }

    _logger.LogInformation("IfcTypeMappingDialogService.BuildRows: {Count} row(s) built.", rows.Count);
    return rows;
  }

  /// <summary>Shows the modal dialog. MUST be called on Revit's main/API thread.</summary>
  public IfcTypeMappingResult? ShowDialog(List<ProfileMappingRow> rows)
  {
    var dialog = new IfcTypeMappingDialog(rows);
    bool? confirmed = dialog.ShowDialog();
    if (confirmed != true)
    {
      _logger.LogInformation("IfcTypeMappingDialogService.ShowDialog: cancelled by the user.");
      return null;
    }

    foreach (var row in rows)
    {
      _logger.LogInformation(
        "IfcTypeMappingDialogService.ShowDialog: row category={Category} ifcTypeName={IfcTypeName} selected={Selected}",
        row.Category,
        row.Profile,
        row.Selected?.DisplayName ?? "(none)"
      );
    }

    var table = BuildEditedTable(rows);
    _logger.LogInformation(
      "IfcTypeMappingDialogService.ShowDialog: confirmed, saveAsDefault={SaveAsDefault}, edited table has {Count} type entr(y/ies): {Entries}",
      dialog.SaveAsDefault,
      table.Types.Count,
      string.Join(", ", table.Types.Select(p => $"{p.Key}={p.Value}"))
    );

    return new IfcTypeMappingResult(table, dialog.SaveAsDefault);
  }

  /// <summary>
  /// Starts from the persisted table (so unrelated hand-added entries survive a "save as default")
  /// and overlays the dialog rows: a selected option sets the composite-key entry, an unselected row
  /// removes it.
  /// </summary>
  private IfcTypeMappingTable BuildEditedTable(List<ProfileMappingRow> rows)
  {
    var persisted = _mappingProvider.PersistedTable;
    var edited = new IfcTypeMappingTable();
    foreach (var pair in persisted.Types)
    {
      edited.Types[pair.Key] = pair.Value;
    }

    foreach (var row in rows)
    {
      string key = $"{row.Category}|{row.Profile}";
      if (row.Selected is { } selected)
      {
        edited.Types[key] = $"{selected.Family}|{selected.Type}";
      }
      else
      {
        edited.Types.Remove(key);
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
