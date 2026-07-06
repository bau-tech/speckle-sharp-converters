using System.ComponentModel;
using DB = Autodesk.Revit.DB;

namespace Speckle.Connectors.Revit.Operations.Receive.ProfileMapping;

/// <summary>One selectable Revit FamilySymbol, identified the same way RevitElementTypeResolver matches by name.</summary>
public sealed record FamilySymbolOption(string Family, string Type)
{
  public string DisplayName => $"{Family} : {Type}";
}

/// <summary>
/// One editable line of the Tekla-profile-mapping dialog: a distinct incoming Tekla profile string
/// (within a category) and the Revit FamilySymbol the user picks for it from what's actually loaded
/// in this document. A null <see cref="Selected"/> means "no explicit mapping" - the converter falls
/// back to its existing heuristics (which, for a non-rectangular profile, means an arbitrary guess).
/// </summary>
public sealed class ProfileMappingRow : INotifyPropertyChanged
{
  private FamilySymbolOption? _selected;

  /// <summary>The category this profile was encountered in, e.g. OST_StructuralFraming.</summary>
  public required DB.BuiltInCategory Category { get; init; }

  /// <summary>The raw Tekla profile string, e.g. "HEA200".</summary>
  public required string Profile { get; init; }

  /// <summary>Human-readable source shown in the grid, e.g. "HEA200  (Structural Framing)".</summary>
  public required string DisplaySource { get; init; }

  /// <summary>FamilySymbols actually loaded in this document for <see cref="Category"/>.</summary>
  public required IReadOnlyList<FamilySymbolOption> Options { get; init; }

  /// <summary>The user's picked FamilySymbol; null = unmapped (heuristics apply).</summary>
  public FamilySymbolOption? Selected
  {
    get => _selected;
    set
    {
      if (_selected != value)
      {
        _selected = value;
        OnPropertyChanged(nameof(Selected));
      }
    }
  }

  public event PropertyChangedEventHandler? PropertyChanged;

  private void OnPropertyChanged(string propertyName) =>
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
