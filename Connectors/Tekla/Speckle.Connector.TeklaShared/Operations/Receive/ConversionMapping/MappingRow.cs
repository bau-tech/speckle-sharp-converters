using System.ComponentModel;

namespace Speckle.Connectors.TeklaShared.Operations.Receive.ConversionMapping;

/// <summary>
/// One editable line of the receive-time conversion table dialog: a Revit family/type (profile
/// row) or structural material name (material row) and the Tekla catalog string it maps to.
/// An empty <see cref="MappedValue"/> means "no explicit mapping" - the converters fall back to
/// their existing heuristics for that entry.
/// </summary>
public sealed class MappingRow : INotifyPropertyChanged
{
  private string _mappedValue = "";
  private bool? _isValid;

  /// <summary>True for profile rows, false for material rows.</summary>
  public bool IsProfile { get; set; }

  /// <summary>Mapping-table key: "{family}|{type}" for profiles, the material name for materials.</summary>
  public string SourceKey { get; set; } = "";

  /// <summary>Human-readable source shown in the grid, e.g. "W Shapes | W310X39  (Structural Framing)".</summary>
  public string DisplaySource { get; set; } = "";

  /// <summary>Dimensional hint from the sender, e.g. "≈165 × 310 mm"; empty when unknown.</summary>
  public string HintText { get; set; } = "";

  /// <summary>The editable Tekla profile/material string; "" = unmapped (heuristics apply).</summary>
  public string MappedValue
  {
    get => _mappedValue;
    set
    {
      if (_mappedValue != value)
      {
        _mappedValue = value;
        OnPropertyChanged(nameof(MappedValue));
      }
    }
  }

  /// <summary>Catalog validation state: null = empty value, true/false from the Tekla catalog.</summary>
  public bool? IsValid
  {
    get => _isValid;
    set
    {
      if (_isValid != value)
      {
        _isValid = value;
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ValidityGlyph));
      }
    }
  }

  public string ValidityGlyph =>
    IsValid switch
    {
      true => "✓",
      false => "✗",
      null => "",
    };

  public event PropertyChangedEventHandler? PropertyChanged;

  private void OnPropertyChanged(string propertyName) =>
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
