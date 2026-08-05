using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace Speckle.Connectors.Revit.Operations.Receive.ProfileMapping;

/// <summary>
/// Modal editor for the receive-time IFC-type mapping: one grid mapping each distinct incoming IFC
/// element type (per category) that doesn't already auto-resolve from a structured profile to a
/// FamilySymbol actually loaded in this document. Built entirely in code (no XAML) - mirrors
/// <see cref="TeklaProfileMappingDialog"/> for the same reason (compiled into several connector
/// csprojs via a shared project, where XAML pages are fragile). Must be constructed and shown on
/// Revit's main/API thread.
/// </summary>
public sealed class IfcTypeMappingDialog : Window
{
  private readonly DataGrid _grid;
  private readonly CheckBox _saveAsDefaultCheckBox;

  public bool SaveAsDefault => _saveAsDefaultCheckBox.IsChecked == true;

  public IfcTypeMappingDialog(List<ProfileMappingRow> rows)
  {
    Title = "Speckle → Revit: IFC Type Mapping";
    Width = 700;
    Height = 560;
    MinWidth = 520;
    MinHeight = 360;
    WindowStartupLocation = WindowStartupLocation.CenterScreen;
    ShowInTaskbar = false;

    var layout = new Grid { Margin = new Thickness(10) };
    layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

    var intro = new TextBlock
    {
      Text =
        "The following IFC element types have no reconstructable cross-section in the source file. "
        + "Pick a loaded family/type for each, or leave blank to keep them as DirectShapes.",
      TextWrapping = TextWrapping.Wrap,
      Margin = new Thickness(0, 0, 0, 8),
    };
    Grid.SetRow(intro, 0);
    layout.Children.Add(intro);

    _grid = CreateGrid(rows);
    Grid.SetRow(_grid, 1);
    layout.Children.Add(_grid);

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
    Grid.SetRow(bottomBar, 2);
    layout.Children.Add(bottomBar);

    Content = layout;
  }

  private void Confirm()
  {
    // commit any cell still being edited so its value reaches the row model before we close
    _grid.CommitEdit(DataGridEditingUnit.Row, true);
    DialogResult = true;
  }

  private static DataGrid CreateGrid(List<ProfileMappingRow> rows)
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
    };

    grid.Columns.Add(
      new DataGridTextColumn
      {
        Header = "IFC type",
        Binding = new Binding(nameof(ProfileMappingRow.DisplaySource)),
        IsReadOnly = true,
        Width = new DataGridLength(1, DataGridLengthUnitType.Star),
      }
    );

    grid.Columns.Add(CreateFamilyTypeColumn());

    return grid;
  }

  /// <summary>
  /// The editable Revit family/type column: a dropdown-only ComboBox (no free text - the target
  /// must be a FamilySymbol that actually exists in this document) populated per-row from
  /// <see cref="ProfileMappingRow.Options"/>, bound to <see cref="ProfileMappingRow.Selected"/>.
  /// </summary>
  private static DataGridTemplateColumn CreateFamilyTypeColumn()
  {
    var editor = new FrameworkElementFactory(typeof(ComboBox));
    editor.SetValue(ComboBox.IsEditableProperty, false);
    editor.SetValue(ComboBox.DisplayMemberPathProperty, nameof(FamilySymbolOption.DisplayName));
    editor.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(ProfileMappingRow.Options)));
    editor.SetBinding(
      Selector.SelectedItemProperty,
      new Binding(nameof(ProfileMappingRow.Selected))
      {
        Mode = BindingMode.TwoWay,
        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
      }
    );

    return new DataGridTemplateColumn
    {
      Header = "Revit family : type",
      Width = new DataGridLength(280),
      // the ComboBox itself handles all input; keep the DataGrid's own edit mode out of the way
      IsReadOnly = true,
      CellTemplate = new DataTemplate { VisualTree = editor },
    };
  }
}
