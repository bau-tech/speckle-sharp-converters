using Speckle.Converters.Common;
using Speckle.Converters.RevitShared.Settings;

namespace Speckle.Converters.RevitShared.Helpers;

/// <summary>
/// Resolves FamilySymbols and Levels in the active receive document for native element reconstruction.
/// Caches FilteredElementCollector results for the lifetime of the receive operation (scoped service).
/// </summary>
public class RevitElementTypeResolver
{
  private readonly IConverterSettingsStore<RevitConversionSettings> _settingsStore;
  private readonly Dictionary<DB.BuiltInCategory, List<DB.FamilySymbol>> _symbolsByCategory = new();
  private List<DB.Level>? _levels;
  private List<DB.WallType>? _wallTypes;
  private List<DB.WallFoundationType>? _wallFoundationTypes;
  private List<DB.FloorType>? _floorTypes;
  private List<DB.RoofType>? _roofTypes;

  public RevitElementTypeResolver(IConverterSettingsStore<RevitConversionSettings> settingsStore)
  {
    _settingsStore = settingsStore;
  }

  /// <summary>
  /// Finds a FamilySymbol matching the given family + type name within a category, falling back to a
  /// type-name-only match, then the first available symbol in the category. Activates the symbol if needed.
  /// </summary>
  public DB.FamilySymbol? FindFamilySymbol(string? family, string? type, DB.BuiltInCategory category)
  {
    List<DB.FamilySymbol> symbols = GetSymbols(category);

    DB.FamilySymbol? symbol = null;
    if (!string.IsNullOrEmpty(family) && !string.IsNullOrEmpty(type))
    {
      symbol = symbols.FirstOrDefault(s => s.FamilyName == family && s.Name == type);
    }

    if (symbol is null && !string.IsNullOrEmpty(type))
    {
      symbol = symbols.FirstOrDefault(s => s.Name == type);
    }

    symbol ??= symbols.FirstOrDefault();

    if (symbol is { IsActive: false })
    {
      symbol.Activate();
      // NewFamilyInstance throws if the symbol isn't active AND regenerated - Activate() alone isn't enough.
      _settingsStore.Current.Document.Regenerate();
    }

    return symbol;
  }

  /// <summary>
  /// Finds a Level by name (matching <c>Level.Name</c>), falling back to the lowest-elevation level.
  /// </summary>
  public DB.Level? FindLevel(string? levelName)
  {
    List<DB.Level> levels = GetLevels();

    if (!string.IsNullOrEmpty(levelName))
    {
      DB.Level? match = levels.FirstOrDefault(l => l.Name == levelName);
      if (match is not null)
      {
        return match;
      }
    }

    return levels.OrderBy(l => l.Elevation).FirstOrDefault();
  }

  /// <summary>
  /// Finds a WallType by name, falling back to the document's default wall type, then the first available.
  /// </summary>
  public DB.WallType? FindWallType(string? typeName)
  {
    List<DB.WallType> wallTypes = GetWallTypes();

    if (!string.IsNullOrEmpty(typeName))
    {
      DB.WallType? match = wallTypes.FirstOrDefault(t => t.Name == typeName);
      if (match is not null)
      {
        return match;
      }
    }

    DB.ElementId defaultId = _settingsStore.Current.Document.GetDefaultElementTypeId(DB.ElementTypeGroup.WallType);
    if (
      defaultId != DB.ElementId.InvalidElementId
      && _settingsStore.Current.Document.GetElement(defaultId) is DB.WallType defaultType
    )
    {
      return defaultType;
    }

    return wallTypes.FirstOrDefault();
  }

  /// <summary>
  /// Finds a WallFoundationType (continuous footing type) by name, falling back to the first available.
  /// </summary>
  public DB.WallFoundationType? FindWallFoundationType(string? typeName)
  {
    List<DB.WallFoundationType> types = GetWallFoundationTypes();

    if (!string.IsNullOrEmpty(typeName))
    {
      DB.WallFoundationType? match = types.FirstOrDefault(t => t.Name == typeName);
      if (match is not null)
      {
        return match;
      }
    }

    return types.FirstOrDefault();
  }

  /// <summary>
  /// Finds a FloorType by name, falling back to the document's default floor type, then the first available.
  /// </summary>
  public DB.FloorType? FindFloorType(string? typeName)
  {
    List<DB.FloorType> floorTypes = GetFloorTypes();

    if (!string.IsNullOrEmpty(typeName))
    {
      DB.FloorType? match = floorTypes.FirstOrDefault(t => t.Name == typeName);
      if (match is not null)
      {
        return match;
      }
    }

    DB.ElementId defaultId = _settingsStore.Current.Document.GetDefaultElementTypeId(DB.ElementTypeGroup.FloorType);
    if (
      defaultId != DB.ElementId.InvalidElementId
      && _settingsStore.Current.Document.GetElement(defaultId) is DB.FloorType defaultType
    )
    {
      return defaultType;
    }

    return floorTypes.FirstOrDefault();
  }

  /// <summary>
  /// Finds a foundation slab FloorType (<see cref="DB.FloorType.IsFoundationSlab"/>) by name, falling back to
  /// the document's default foundation slab type, then any foundation slab type.
  /// </summary>
  public DB.FloorType? FindFoundationSlabType(string? typeName)
  {
    List<DB.FloorType> floorTypes = GetFloorTypes();

    if (!string.IsNullOrEmpty(typeName))
    {
      DB.FloorType? match = floorTypes.FirstOrDefault(t => t.Name == typeName && t.IsFoundationSlab);
      if (match is not null)
      {
        return match;
      }
    }

    DB.ElementId defaultId = DB.Floor.GetDefaultFloorType(_settingsStore.Current.Document, true);
    if (
      defaultId != DB.ElementId.InvalidElementId
      && _settingsStore.Current.Document.GetElement(defaultId) is DB.FloorType defaultType
    )
    {
      return defaultType;
    }

    return floorTypes.FirstOrDefault(t => t.IsFoundationSlab);
  }

  /// <summary>
  /// Finds a RoofType by name, falling back to the document's default roof type, then the first available.
  /// </summary>
  public DB.RoofType? FindRoofType(string? typeName)
  {
    List<DB.RoofType> roofTypes = GetRoofTypes();

    if (!string.IsNullOrEmpty(typeName))
    {
      DB.RoofType? match = roofTypes.FirstOrDefault(t => t.Name == typeName);
      if (match is not null)
      {
        return match;
      }
    }

    DB.ElementId defaultId = _settingsStore.Current.Document.GetDefaultElementTypeId(DB.ElementTypeGroup.RoofType);
    if (
      defaultId != DB.ElementId.InvalidElementId
      && _settingsStore.Current.Document.GetElement(defaultId) is DB.RoofType defaultType
    )
    {
      return defaultType;
    }

    return roofTypes.FirstOrDefault();
  }

  private List<DB.FamilySymbol> GetSymbols(DB.BuiltInCategory category)
  {
    if (!_symbolsByCategory.TryGetValue(category, out List<DB.FamilySymbol>? symbols))
    {
      using var collector = new DB.FilteredElementCollector(_settingsStore.Current.Document);
      symbols = collector.OfClass(typeof(DB.FamilySymbol)).OfCategory(category).Cast<DB.FamilySymbol>().ToList();
      _symbolsByCategory[category] = symbols;
    }

    return symbols;
  }

  private List<DB.Level> GetLevels()
  {
    if (_levels is null)
    {
      using var collector = new DB.FilteredElementCollector(_settingsStore.Current.Document);
      _levels = collector.OfClass(typeof(DB.Level)).Cast<DB.Level>().ToList();
    }

    return _levels;
  }

  private List<DB.WallType> GetWallTypes()
  {
    if (_wallTypes is null)
    {
      using var collector = new DB.FilteredElementCollector(_settingsStore.Current.Document);
      _wallTypes = collector.OfClass(typeof(DB.WallType)).Cast<DB.WallType>().ToList();
    }

    return _wallTypes;
  }

  private List<DB.WallFoundationType> GetWallFoundationTypes()
  {
    if (_wallFoundationTypes is null)
    {
      using var collector = new DB.FilteredElementCollector(_settingsStore.Current.Document);
      _wallFoundationTypes = collector.OfClass(typeof(DB.WallFoundationType)).Cast<DB.WallFoundationType>().ToList();
    }

    return _wallFoundationTypes;
  }

  private List<DB.FloorType> GetFloorTypes()
  {
    if (_floorTypes is null)
    {
      using var collector = new DB.FilteredElementCollector(_settingsStore.Current.Document);
      _floorTypes = collector.OfClass(typeof(DB.FloorType)).Cast<DB.FloorType>().ToList();
    }

    return _floorTypes;
  }

  private List<DB.RoofType> GetRoofTypes()
  {
    if (_roofTypes is null)
    {
      using var collector = new DB.FilteredElementCollector(_settingsStore.Current.Document);
      _roofTypes = collector.OfClass(typeof(DB.RoofType)).Cast<DB.RoofType>().ToList();
    }

    return _roofTypes;
  }
}
