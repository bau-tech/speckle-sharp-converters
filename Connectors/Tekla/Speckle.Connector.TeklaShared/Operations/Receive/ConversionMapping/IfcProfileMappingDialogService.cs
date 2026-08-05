using Microsoft.Extensions.Logging;
using Speckle.Converters.TeklaShared.Helpers.ProfileMapping;
using Speckle.Objects.Data;

namespace Speckle.Connectors.TeklaShared.Operations.Receive.ConversionMapping;

/// <summary>Outcome of a confirmed IFC mapping dialog; null result from ShowDialog means the user cancelled.</summary>
public sealed class IfcProfileMappingResult
{
  public IfcProfileMappingResult(Dictionary<string, string> table, bool saveAsDefault)
  {
    Table = table;
    SaveAsDefault = saveAsDefault;
  }

  public Dictionary<string, string> Table { get; }
  public bool SaveAsDefault { get; }
}

/// <summary>
/// Builds the rows for and shows the receive-time IFC-profile-mapping dialog: one row per distinct
/// <c>ifcTypeName</c> that the IFC native-reconstruction enricher (shared with the Revit connector -
/// see <c>RevitNativeSchemaEnricher</c>) couldn't already resolve a clean profile string for (piles,
/// non-rectangular/non-circular beams/columns - see <c>IfcColumnBeamToTeklaBeamConverter</c>/
/// <c>IfcFoundationToTeklaConverter</c>'s own remarks). Deliberately a separate, isolated service
/// from <see cref="ConversionMappingDialogService"/> (a different source - IFC, not the Revit→Tekla
/// roundtrip - with its own mapping table), but reuses the generic <see cref="MappingRow"/>/
/// <see cref="ConversionMappingDialog"/> view since a Tekla profile is a free-text catalog string
/// either way, unlike Revit's own IFC dialog (which picks a FamilySymbol from a fixed list).
/// </summary>
public sealed class IfcProfileMappingDialogService
{
  // Categories whose Tekla converter actually consults IfcProfileMappingProvider - mirrors
  // IfcColumnBeamToTeklaBeamConverter (columns/beams) and IfcFoundationToTeklaConverter's pile path
  // exactly. Walls/Floors never consult it (their profile always comes directly from extracted
  // geometry), so they're never scanned here.
  private static readonly HashSet<string> s_mappableCategories = new(StringComparer.Ordinal)
  {
    "OST_StructuralColumns",
    "OST_StructuralFraming",
    "OST_StructuralFoundation",
  };

  private readonly IfcProfileMappingProvider _mappingProvider;
  private readonly TeklaCatalogValidator _validator;
  private readonly ILogger<IfcProfileMappingDialogService> _logger;

  public IfcProfileMappingDialogService(
    IfcProfileMappingProvider mappingProvider,
    TeklaCatalogValidator validator,
    ILogger<IfcProfileMappingDialogService> logger
  )
  {
    _mappingProvider = mappingProvider;
    _validator = validator;
    _logger = logger;
  }

  public List<MappingRow> BuildRows(IReadOnlyList<DataObject> ifcObjects)
  {
    var rows = new List<MappingRow>();
    var persisted = _mappingProvider.GetPersistedTable();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var dataObject in ifcObjects)
    {
      if (
        dataObject["builtInCategory"] is not string category
        || !s_mappableCategories.Contains(category)
        || dataObject["ifcTypeName"] is not string ifcTypeName
        || string.IsNullOrWhiteSpace(ifcTypeName)
      )
      {
        continue;
      }

      // Already resolved cleanly from the IFC extrusion/profile geometry - a mapping here would
      // have no effect (see IfcColumnBeamToTeklaBeamConverter.ResolveProfile: the enricher's own
      // profile string always wins first).
      if (!string.IsNullOrWhiteSpace(dataObject["profile"] as string))
      {
        continue;
      }

      if (!seen.Add(ifcTypeName))
      {
        continue;
      }

      // Already resolved via a previously saved default - no need to re-prompt for it.
      if (persisted.ContainsKey(ifcTypeName))
      {
        continue;
      }

      var row = new MappingRow
      {
        IsProfile = true,
        SourceKey = ifcTypeName,
        DisplaySource = $"{ifcTypeName}  ({CategoryDisplayName(category)})",
        MappedValue = "",
      };
      row.IsValid = null;
      rows.Add(row);
    }

    _logger.LogInformation("IfcProfileMappingDialogService.BuildRows: {Count} row(s) built.", rows.Count);
    return rows;
  }

  /// <summary>Shows the modal dialog. MUST be called on the Tekla UI thread.</summary>
  public IfcProfileMappingResult? ShowDialog(List<MappingRow> rows)
  {
    var dialog = new ConversionMappingDialog(rows, _validator.GetCatalogProfileNames(), []);
    bool? confirmed = dialog.ShowDialog();
    if (confirmed != true)
    {
      _logger.LogInformation("IfcProfileMappingDialogService.ShowDialog: cancelled by the user.");
      return null;
    }

    return new IfcProfileMappingResult(BuildEditedTable(rows), dialog.SaveAsDefault);
  }

  /// <summary>
  /// Starts from the persisted table (so unrelated hand-added entries survive a "save as default")
  /// and overlays the dialog rows: non-empty values are set under the ifcTypeName key, cleared rows
  /// remove their entry.
  /// </summary>
  private Dictionary<string, string> BuildEditedTable(List<MappingRow> rows)
  {
    var edited = new Dictionary<string, string>(_mappingProvider.GetPersistedTable(), StringComparer.Ordinal);

    foreach (var row in rows)
    {
      string value = row.MappedValue.Trim();
      if (value.Length > 0)
      {
        edited[row.SourceKey] = value;
      }
      else
      {
        edited.Remove(row.SourceKey);
      }
    }

    return edited;
  }

  private static string CategoryDisplayName(string builtInCategory) =>
    builtInCategory switch
    {
      "OST_StructuralColumns" => "Column",
      "OST_StructuralFoundation" => "Foundation",
      _ => "Structural Framing",
    };
}
