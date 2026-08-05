using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;

namespace Speckle.Connectors.TeklaShared.Operations.Receive.ConversionMapping;

/// <summary>
/// Modal editor for the receive-time conversion table: two grids (Revit family/type → Tekla
/// profile, Revit material → Tekla material) with live catalog-validation glyphs. Built entirely
/// in code (no XAML) because this file is compiled into three connector csprojs via a shared
/// project, where XAML pages are fragile. Must be constructed and shown on the Tekla UI thread.
/// </summary>
public sealed class ConversionMappingDialog : Window
{
  private readonly DataGrid _profileGrid;
  private readonly DataGrid _materialGrid;
  private readonly CheckBox _saveAsDefaultCheckBox;

  public bool SaveAsDefault => _saveAsDefaultCheckBox.IsChecked == true;

  public ConversionMappingDialog(
    List<MappingRow> rows,
    IReadOnlyList<string> catalogProfileNames,
    IReadOnlyList<string> catalogMaterialNames
  )
  {
    Title = "Speckle → Tekla: Conversion Table";
    Width = 780;
    Height = 620;
    MinWidth = 560;
    MinHeight = 400;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    ShowInTaskbar = false;

    try
    {
      new WindowInteropHelper(this).Owner = Tekla.Structures.Dialog.MainWindow.Frame.Handle;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      // no Tekla main window handle (e.g. very early startup) - dialog still works, just unowned
      WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    var profileRows = new List<MappingRow>();
    var materialRows = new List<MappingRow>();
    foreach (var row in rows)
    {
      (row.IsProfile ? profileRows : materialRows).Add(row);
    }

    _profileGrid = CreateGrid(profileRows, isProfile: true, catalogProfileNames);
    _materialGrid = CreateGrid(materialRows, isProfile: false, catalogMaterialNames);

    var layout = new Grid { Margin = new Thickness(10) };
    layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
    layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
    layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

    var intro = new TextBlock
    {
      Text =
        "Review how the received Revit types and materials are converted to Tekla. "
        + "Empty cells fall back to automatic resolution; ✓/✗ shows whether a value exists in the Tekla catalog. "
        + "Rows marked \"auto only\" are always resolved automatically - their profile field is disabled "
        + "because a mapping would have no effect.",
      TextWrapping = TextWrapping.Wrap,
      Margin = new Thickness(0, 0, 0, 8),
    };
    Grid.SetRow(intro, 0);
    layout.Children.Add(intro);

    var profilesLabel = MakeSectionLabel($"Profiles ({profileRows.Count})");
    Grid.SetRow(profilesLabel, 1);
    layout.Children.Add(profilesLabel);
    Grid.SetRow(_profileGrid, 2);
    layout.Children.Add(_profileGrid);

    var materialsLabel = MakeSectionLabel($"Materials ({materialRows.Count})");
    Grid.SetRow(materialsLabel, 3);
    layout.Children.Add(materialsLabel);
    Grid.SetRow(_materialGrid, 4);
    layout.Children.Add(_materialGrid);

    _saveAsDefaultCheckBox = new CheckBox
    {
      Content = "Save as default mapping",
      VerticalAlignment = VerticalAlignment.Center,
    };

    var receiveButton = new Button
    {
      Content = "Receive",
      IsDefault = true,
      MinWidth = 90,
      Margin = new Thickness(8, 0, 0, 0),
      Padding = new Thickness(12, 4, 12, 4),
    };
    receiveButton.Click += (_, _) => Confirm();

    var cancelButton = new Button
    {
      Content = "Cancel",
      IsCancel = true,
      MinWidth = 90,
      Margin = new Thickness(8, 0, 0, 0),
      Padding = new Thickness(12, 4, 12, 4),
    };

    var buttonPanel = new StackPanel
    {
      Orientation = Orientation.Horizontal,
      HorizontalAlignment = HorizontalAlignment.Right,
    };
    buttonPanel.Children.Add(cancelButton);
    buttonPanel.Children.Add(receiveButton);

    var bottomBar = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
    DockPanel.SetDock(_saveAsDefaultCheckBox, Dock.Left);
    bottomBar.Children.Add(_saveAsDefaultCheckBox);
    bottomBar.Children.Add(buttonPanel);
    Grid.SetRow(bottomBar, 5);
    layout.Children.Add(bottomBar);

    Content = layout;
  }

  private void Confirm()
  {
    // commit any cell still being edited so its value reaches the row model before we close
    _profileGrid.CommitEdit(DataGridEditingUnit.Row, true);
    _materialGrid.CommitEdit(DataGridEditingUnit.Row, true);
    DialogResult = true;
  }

  private static TextBlock MakeSectionLabel(string text) =>
    new()
    {
      Text = text,
      FontWeight = FontWeights.Bold,
      Margin = new Thickness(0, 4, 0, 2),
    };

  private static DataGrid CreateGrid(List<MappingRow> rows, bool isProfile, IReadOnlyList<string> catalogNames)
  {
    var grid = new DataGrid
    {
      ItemsSource = rows,
      AutoGenerateColumns = false,
      CanUserAddRows = false,
      CanUserDeleteRows = false,
      HeadersVisibility = DataGridHeadersVisibility.Column,
      RowHeaderWidth = 0,
      GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
      SelectionMode = DataGridSelectionMode.Single,
      SelectionUnit = DataGridSelectionUnit.Cell,
      Margin = new Thickness(0, 0, 0, 4),
    };

    grid.Columns.Add(
      new DataGridTextColumn
      {
        Header = "Revit source",
        Binding = new Binding(nameof(MappingRow.DisplaySource)),
        IsReadOnly = true,
        Width = new DataGridLength(1, DataGridLengthUnitType.Star),
      }
    );

    if (isProfile)
    {
      grid.Columns.Add(
        new DataGridTextColumn
        {
          Header = "Hint",
          Binding = new Binding(nameof(MappingRow.HintText)),
          IsReadOnly = true,
          Width = DataGridLength.Auto,
        }
      );

      // Only profile rows can be non-mappable (see MappingRow.IsMappable remarks) - material rows
      // are always mappable, so this column would be dead weight in the materials grid.
      var mappableStyle = new Style(typeof(TextBlock));
      mappableStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
      mappableStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.Gray));
      mappableStyle.Setters.Add(new Setter(TextBlock.FontStyleProperty, FontStyles.Italic));
      mappableStyle.Setters.Add(
        new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(MappingRow.MappabilityNote)))
      );
      grid.Columns.Add(
        new DataGridTextColumn
        {
          Header = "",
          Binding = new Binding(nameof(MappingRow.MappabilityGlyph)),
          IsReadOnly = true,
          Width = DataGridLength.Auto,
          ElementStyle = mappableStyle,
        }
      );
    }

    grid.Columns.Add(CreateMappedValueColumn(isProfile ? "Tekla profile" : "Tekla material", catalogNames));

    var glyphStyle = new Style(typeof(TextBlock));
    glyphStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
    var validTrigger = new DataTrigger { Binding = new Binding(nameof(MappingRow.IsValid)), Value = true };
    validTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.Green));
    var invalidTrigger = new DataTrigger { Binding = new Binding(nameof(MappingRow.IsValid)), Value = false };
    invalidTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.Red));
    glyphStyle.Triggers.Add(validTrigger);
    glyphStyle.Triggers.Add(invalidTrigger);

    grid.Columns.Add(
      new DataGridTextColumn
      {
        Header = "",
        Binding = new Binding(nameof(MappingRow.ValidityGlyph)),
        IsReadOnly = true,
        Width = new DataGridLength(32),
        ElementStyle = glyphStyle,
      }
    );

    return grid;
  }

  /// <summary>
  /// The editable Tekla profile/material column: an always-visible editable ComboBox per row -
  /// type any value directly (free text, e.g. parametric profiles like "800*400"), or double-click
  /// the cell (or use the arrow button) to open the dropdown populated with the installed
  /// catalog's names (virtualized - profile catalogs can hold tens of thousands of entries).
  /// The Text binding updates on every keystroke so the validation glyph reacts live to both
  /// typing and dropdown picks.
  /// </summary>
  /// <remarks>
  /// The dropdown ALSO live-filters to matches as the user types (see <see cref="MatchesSearch"/>),
  /// rather than showing the full unfiltered catalog - an editable WPF ComboBox's built-in
  /// IsTextSearchEnabled only jumps to the first alphabetically-matching item, which isn't a usable
  /// search over a catalog with thousands of entries. Matching ignores spaces on both sides (Revit's
  /// captured designation is commonly "HEA 300" with a space; Tekla's own catalog name is "HEA300"
  /// without one), so typing either finds the same real catalog entry.
  /// </remarks>
  private static DataGridTemplateColumn CreateMappedValueColumn(string header, IReadOnlyList<string> catalogNames)
  {
    var editor = new FrameworkElementFactory(typeof(ComboBox));
    editor.SetValue(ComboBox.IsEditableProperty, true);
    editor.SetValue(ComboBox.StaysOpenOnEditProperty, true);
    editor.AddHandler(
      Control.MouseDoubleClickEvent,
      new System.Windows.Input.MouseButtonEventHandler(
        (sender, _) =>
        {
          if (sender is ComboBox comboBox)
          {
            comboBox.ItemsSource = catalogNames;
            comboBox.IsDropDownOpen = true;
          }
        }
      )
    );
    // Live search-as-you-type: TextChanged bubbles up from the editable ComboBox's own internal
    // TextBox part, so this can be attached directly on the ComboBox itself with no template lookup.
    editor.AddHandler(
      TextBoxBase.TextChangedEvent,
      new TextChangedEventHandler(
        (sender, _) =>
        {
          if (sender is not ComboBox comboBox)
          {
            return;
          }

          string searchText = comboBox.Text;
          var filtered =
            searchText.Length == 0
              ? catalogNames
              : catalogNames.Where(name => MatchesSearch(name, searchText)).ToList();
          comboBox.ItemsSource = filtered;
          if (searchText.Length > 0 && filtered.Count > 0)
          {
            comboBox.IsDropDownOpen = true;
          }
        }
      )
    );
    editor.SetValue(ComboBox.MaxDropDownHeightProperty, 320.0);
    editor.SetValue(ItemsControl.ItemsSourceProperty, catalogNames);
    editor.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
    editor.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
    editor.SetValue(ScrollViewer.CanContentScrollProperty, true);
    var itemsPanel = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
    editor.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate { VisualTree = itemsPanel });
    editor.SetBinding(
      ComboBox.TextProperty,
      new Binding(nameof(MappingRow.MappedValue))
      {
        Mode = BindingMode.TwoWay,
        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
      }
    );
    // Non-mappable rows (see MappingRow.IsMappable remarks) get their editor disabled outright -
    // no point letting the user type a value the converter will never read.
    editor.SetBinding(Control.IsEnabledProperty, new Binding(nameof(MappingRow.IsMappable)));
    editor.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(MappingRow.MappabilityNote)));

    return new DataGridTemplateColumn
    {
      Header = header,
      Width = new DataGridLength(180),
      // the ComboBox itself handles all input; keep the DataGrid's own edit mode out of the way
      IsReadOnly = true,
      CellTemplate = new DataTemplate { VisualTree = editor },
    };
  }

  // Space-insensitive substring match - "HEA 300" (a commonly hand-typed or Revit-captured form)
  // and "HEA300" (Tekla's actual catalog name) must both find the same real catalog entry.
  private static bool MatchesSearch(string catalogName, string searchText) =>
    catalogName.Replace(" ", "").IndexOf(searchText.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) >= 0;
}
