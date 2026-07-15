using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Speckle.Connectors.Common.Operations;
using Speckle.Converters.TeklaShared.Helpers;
using Speckle.Converters.TeklaShared.Helpers.ProfileMapping;
using Speckle.Objects.Data;

namespace Speckle.Connectors.TeklaShared.Operations.Receive.ConversionMapping;

/// <summary>Outcome of a confirmed mapping dialog; null result from ShowDialog means the user cancelled.</summary>
public sealed class MappingDialogResult
{
  public MappingDialogResult(RevitProfileMaterialMappingTable table, bool saveAsDefault)
  {
    Table = table;
    SaveAsDefault = saveAsDefault;
  }

  public RevitProfileMaterialMappingTable Table { get; }
  public bool SaveAsDefault { get; }
}

/// <summary>
/// Builds the rows for and shows the receive-time conversion table dialog. Rows are the union of
/// the sender's conversion table (from the root object) and a scan of the received RevitObjects
/// (so commits sent before the table existed still get the dialog), pre-filled from the persisted
/// JSON mapping and validator-checked suggestions.
/// </summary>
public sealed class ConversionMappingDialogService
{
  private readonly RevitProfileMaterialMappingProvider _mappingProvider;
  private readonly TeklaCatalogValidator _validator;
  private readonly ILogger<ConversionMappingDialogService> _logger;

  public ConversionMappingDialogService(
    RevitProfileMaterialMappingProvider mappingProvider,
    TeklaCatalogValidator validator,
    ILogger<ConversionMappingDialogService> logger
  )
  {
    _mappingProvider = mappingProvider;
    _validator = validator;
    _logger = logger;
  }

  public List<MappingRow> BuildRows(ConversionTable? serverTable, IReadOnlyList<RevitObject> revitObjects)
  {
    var rows = new List<MappingRow>();
    var persisted = _mappingProvider.GetPersistedTable();
    var seenProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var seenMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    if (serverTable is not null)
    {
      foreach (var entry in serverTable.Profiles)
      {
        AddProfileRow(
          rows,
          seenProfiles,
          persisted,
          entry.Family,
          entry.Type,
          entry.Category,
          entry.BuiltInCategory,
          entry.WidthMm,
          entry.HeightMm
        );
      }
      foreach (var entry in serverTable.Materials)
      {
        AddMaterialRow(rows, seenMaterials, persisted, entry.Name);
      }
    }

    foreach (var revitObject in revitObjects)
    {
      // openings become boolean cuts on their hosts - they have no profile/material of their own
      if (revitObject.category.IndexOf("Opening", StringComparison.OrdinalIgnoreCase) >= 0)
      {
        continue;
      }

      AddProfileRow(
        rows,
        seenProfiles,
        persisted,
        revitObject.family,
        revitObject.type,
        revitObject.category,
        revitObject["builtInCategory"] as string ?? "",
        null,
        null
      );

      if (
        RevitPropertyReader.TryGetStructuralMaterialName(revitObject, out string? materialName)
        && !string.IsNullOrWhiteSpace(materialName)
      )
      {
        AddMaterialRow(rows, seenMaterials, persisted, materialName!);
      }
    }

    return rows;
  }

  /// <summary>
  /// Shows the modal dialog. MUST be called on the Tekla UI thread. Returns null when the user
  /// cancels; otherwise the edited table (persisted table merged with the row values) and whether
  /// the user asked to save it as the new default.
  /// </summary>
  public MappingDialogResult? ShowDialog(List<MappingRow> rows)
  {
    PropertyChangedEventHandler onRowChanged = (sender, e) =>
    {
      if (e.PropertyName == nameof(MappingRow.MappedValue) && sender is MappingRow row)
      {
        ValidateRow(row);
      }
    };

    foreach (var row in rows)
    {
      row.PropertyChanged += onRowChanged;
    }

    try
    {
      var dialog = new ConversionMappingDialog(
        rows,
        _validator.GetCatalogProfileNames(),
        _validator.GetCatalogMaterialNames()
      );
      bool? confirmed = dialog.ShowDialog();
      if (confirmed != true)
      {
        return null;
      }

      return new MappingDialogResult(BuildEditedTable(rows), dialog.SaveAsDefault);
    }
    finally
    {
      foreach (var row in rows)
      {
        row.PropertyChanged -= onRowChanged;
      }
    }
  }

  private void ValidateRow(MappingRow row)
  {
    string value = row.MappedValue.Trim();
    if (value.Length == 0)
    {
      row.IsValid = null;
      return;
    }

    try
    {
      row.IsValid = row.IsProfile ? _validator.IsValidProfile(value) : _validator.IsValidMaterial(value);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      // the validator itself fails open; this is just belt-and-braces so typing can never crash the dialog
      _logger.LogWarning(ex, "Validation failed for '{Value}'; showing it as valid.", value);
      row.IsValid = true;
    }
  }

  /// <summary>
  /// Starts from the persisted table (so unrelated hand-added entries survive a "save as default")
  /// and overlays the dialog rows: non-empty values are set under the composite key, cleared rows
  /// remove their composite-key entry.
  /// </summary>
  private RevitProfileMaterialMappingTable BuildEditedTable(List<MappingRow> rows)
  {
    var persisted = _mappingProvider.GetPersistedTable();
    var edited = new RevitProfileMaterialMappingTable();
    foreach (var pair in persisted.Profiles)
    {
      edited.Profiles[pair.Key] = pair.Value;
    }
    foreach (var pair in persisted.Materials)
    {
      edited.Materials[pair.Key] = pair.Value;
    }

    foreach (var row in rows)
    {
      var target = row.IsProfile ? edited.Profiles : edited.Materials;
      string value = row.MappedValue.Trim();
      if (value.Length > 0)
      {
        target[row.SourceKey] = value;
      }
      else
      {
        target.Remove(row.SourceKey);
      }
    }

    return edited;
  }

  // Categories whose Tekla converter never consults the profile mapping table - see each
  // converter's own remarks. Currently only Walls (RevitWallToTeklaBeamConverter: "No
  // profile-catalog mapping is involved - the rectangular section comes directly from the wall's
  // own dimensions"). Columns/Beams, Floors, and all Foundations (pad/pile/strip/wall footings)
  // all try the mapping table first.
  private static readonly HashSet<string> s_nonMappableProfileCategories = new(StringComparer.Ordinal)
  {
    "OST_Walls",
  };

  private void AddProfileRow(
    List<MappingRow> rows,
    HashSet<string> seen,
    RevitProfileMaterialMappingTable persisted,
    string family,
    string type,
    string category,
    string builtInCategory,
    double? widthMm,
    double? heightMm
  )
  {
    if (string.IsNullOrWhiteSpace(type))
    {
      return;
    }

    string key = $"{family}|{type}";
    if (!seen.Add(key))
    {
      return;
    }

    string prefill = "";
    if (persisted.Profiles.TryGetValue(key, out string? mapped) || persisted.Profiles.TryGetValue(type, out mapped))
    {
      prefill = mapped ?? "";
    }
    else
    {
      foreach (string candidate in SuggestProfiles(type, widthMm, heightMm))
      {
        if (_validator.IsValidProfile(candidate))
        {
          prefill = candidate;
          break;
        }
      }
    }

    string displaySource = string.IsNullOrWhiteSpace(family) ? type : $"{family} | {type}";
    if (!string.IsNullOrWhiteSpace(category))
    {
      displaySource = $"{displaySource}  ({category})";
    }

    bool isMappable = !s_nonMappableProfileCategories.Contains(builtInCategory);

    var row = new MappingRow
    {
      IsProfile = true,
      SourceKey = key,
      DisplaySource = displaySource,
      HintText =
        widthMm is not null && heightMm is not null
          ? string.Format(CultureInfo.InvariantCulture, "≈{0:0.#} × {1:0.#} mm", widthMm.Value, heightMm.Value)
          : "",
      MappedValue = prefill,
      IsMappable = isMappable,
      MappabilityNote = isMappable
        ? ""
        : "This element's profile is always derived from its own dimensions - a mapping here has no effect.",
    };
    row.IsValid = prefill.Length == 0 ? null : _validator.IsValidProfile(prefill);
    rows.Add(row);
  }

  private void AddMaterialRow(
    List<MappingRow> rows,
    HashSet<string> seen,
    RevitProfileMaterialMappingTable persisted,
    string name
  )
  {
    if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
    {
      return;
    }

    string prefill = "";
    if (persisted.Materials.TryGetValue(name, out string? mapped))
    {
      prefill = mapped ?? "";
    }
    else if (_validator.IsValidMaterial(name))
    {
      // the Revit material name may already be a valid Tekla alias ("S235", "C30/37")
      prefill = name;
    }

    var row = new MappingRow
    {
      IsProfile = false,
      SourceKey = name,
      DisplaySource = name,
      MappedValue = prefill,
    };
    row.IsValid = prefill.Length == 0 ? null : _validator.IsValidMaterial(prefill);
    rows.Add(row);
  }

  private static IEnumerable<string> SuggestProfiles(string type, double? widthMm, double? heightMm)
  {
    yield return type;

    string stripped = type.Replace(" ", "");
    if (stripped != type)
    {
      yield return stripped;
    }

    // same "{height}*{width}" shape the converters build for parametric sections
    if (RevitPropertyReader.TryParseSectionFromName(type, out double nameWidthMm, out double nameHeightMm))
    {
      yield return $"{nameHeightMm:0.#}*{nameWidthMm:0.#}";
    }

    if (widthMm is not null && heightMm is not null && widthMm.Value > 0 && heightMm.Value > 0)
    {
      yield return $"{heightMm.Value:0.#}*{widthMm.Value:0.#}";
    }
  }
}
