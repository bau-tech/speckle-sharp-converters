namespace Build;

internal static class Consts
{
  public static readonly string[] Solutions = ["Speckle.Connectors.slnx"];

  public static readonly ProjectGroup[] ProjectGroups =
  {
    new(
      "revit",
      [
        new("Connectors/Revit/Speckle.Connectors.Revit2023", "net48"),
        new("Connectors/Revit/Speckle.Connectors.Revit2024", "net48"),
        new("Connectors/Revit/Speckle.Connectors.Revit2025", "net8.0-windows"),
        new("Connectors/Revit/Speckle.Connectors.Revit2026", "net8.0-windows"),
        new("Connectors/Revit/Speckle.Connectors.Revit2027", "net10.0-windows"),
      ]
    ),
    new(
      "teklastructures",
      [
        new("Connectors/Tekla/Speckle.Connector.Tekla2023", "net48"),
        new("Connectors/Tekla/Speckle.Connector.Tekla2024", "net48"),
        new("Connectors/Tekla/Speckle.Connector.Tekla2025", "net48"),
        new("Connectors/Tekla/Speckle.Connector.Tekla2026", "net48"),
      ]
    ),
  };
}

internal readonly record struct ProjectGroup(string HostAppSlug, IReadOnlyList<InstallerAsset> Projects)
{
  public override string ToString() => $"{HostAppSlug}";
}

internal readonly record struct InstallerAsset(string ProjectPath, string TargetName);
