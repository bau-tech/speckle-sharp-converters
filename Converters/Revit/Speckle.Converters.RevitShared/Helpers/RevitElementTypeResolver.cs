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
  private readonly Dictionary<(DB.BuiltInCategory Category, double WidthMm, double HeightMm), DB.FamilySymbol> _rectangularSymbolCache =
    new();
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

    return ActivateIfNeeded(symbol);
  }

  /// <summary>
  /// Finds a FamilySymbol matching family + type EXACTLY, with no name-only or first-available
  /// fallback - unlike <see cref="FindFamilySymbol"/>, a miss returns null instead of guessing. Used
  /// for explicit user-confirmed mappings (see TeklaProfileMappingProvider): a stale mapping (the
  /// named family/type no longer exists in this document) should be visibly absent so the caller
  /// falls through to its next resolution candidate, not silently resolved to the wrong symbol.
  /// </summary>
  public DB.FamilySymbol? FindExactFamilySymbol(string family, string type, DB.BuiltInCategory category) =>
    ActivateIfNeeded(GetSymbols(category).FirstOrDefault(s => s.FamilyName == family && s.Name == type));

  private DB.FamilySymbol? ActivateIfNeeded(DB.FamilySymbol? symbol)
  {
    if (symbol is { IsActive: false })
    {
      symbol.Activate();
      // NewFamilyInstance throws if the symbol isn't active AND regenerated - Activate() alone isn't enough.
      _settingsStore.Current.Document.Regenerate();
    }

    return symbol;
  }

  // Family-author-chosen dimension parameter names vary by template/company convention and are not
  // necessarily English or lowercase - e.g. a German "STB Träger - rechteckig" catalog family observed
  // in testing uses uppercase "B"/"H", not "b"/"h". Mirrors the candidate lists already used on the
  // Tekla-side send heuristic (RevitColumnBeamToTeklaBeamConverter.s_widthParamNames/s_heightParamNames).
  private static readonly string[] s_widthParamNames = ["b", "B", "Breite", "Width"];
  private static readonly string[] s_heightParamNames = ["h", "H", "Höhe", "Hoehe", "Height"];

  private static DB.Parameter? FindLengthParameter(DB.FamilySymbol symbol, string[] candidateNames)
  {
    foreach (string name in candidateNames)
    {
      DB.Parameter? param = symbol.LookupParameter(name);
      if (param is not null)
      {
        return param;
      }
    }
    return null;
  }

  private static bool MatchesDimensions(DB.FamilySymbol symbol, double widthMm, double heightMm)
  {
    DB.Parameter? widthParam = FindLengthParameter(symbol, s_widthParamNames);
    DB.Parameter? heightParam = FindLengthParameter(symbol, s_heightParamNames);
    if (widthParam is null || heightParam is null)
    {
      return false;
    }

    double actualWidthMm = DB.UnitUtils.ConvertFromInternalUnits(widthParam.AsDouble(), DB.UnitTypeId.Millimeters);
    double actualHeightMm = DB.UnitUtils.ConvertFromInternalUnits(heightParam.AsDouble(), DB.UnitTypeId.Millimeters);
    return Math.Abs(actualWidthMm - widthMm) < DIMENSION_TOLERANCE_MM
      && Math.Abs(actualHeightMm - heightMm) < DIMENSION_TOLERANCE_MM;
  }

  // Sub-mm tolerance for comparing a requested dimension against a family type's parameter value -
  // absorbs mm/feet unit round-trip and floating-point noise, not a real size difference.
  private const double DIMENSION_TOLERANCE_MM = 0.5;

  /// <summary>
  /// Spike: finds an existing rectangular-profile symbol already dimensioned to widthMm/heightMm -
  /// preferring any already-loaded Revit family/type whose own width/height parameters already match
  /// (so a real project family is reused instead of cluttering the model with a redundant duplicate),
  /// then a symbol this same mechanism previously synthesized (e.g. from an earlier receive into this
  /// document), or as a last resort creates one by duplicating the first symbol in the category whose
  /// family exposes both a width and a height type parameter (see <see cref="s_widthParamNames"/>/
  /// <see cref="s_heightParamNames"/> for the recognized name variants) and setting those parameters
  /// to the requested dimensions. Returns null if no such template is loaded in the category - callers
  /// should fall back to their existing resolution logic.
  /// </summary>
  public DB.FamilySymbol? FindOrCreateRectangularSymbol(DB.BuiltInCategory category, double widthMm, double heightMm)
  {
    var cacheKey = (category, widthMm, heightMm);
    if (_rectangularSymbolCache.TryGetValue(cacheKey, out DB.FamilySymbol? cached))
    {
      return cached;
    }

    List<DB.FamilySymbol> symbols = GetSymbols(category);

    DB.FamilySymbol? matchByDimensions = symbols.FirstOrDefault(s => MatchesDimensions(s, widthMm, heightMm));
    if (matchByDimensions is not null)
    {
      if (matchByDimensions is { IsActive: false })
      {
        matchByDimensions.Activate();
        _settingsStore.Current.Document.Regenerate();
      }
      _rectangularSymbolCache[cacheKey] = matchByDimensions;
      return matchByDimensions;
    }

    string newName = $"{widthMm:0.#}x{heightMm:0.#} (Tekla)";

    // A previous receive into this document may have already created this exact size - reuse it
    // rather than letting Duplicate() throw on a name collision.
    DB.FamilySymbol? existing = symbols.FirstOrDefault(s => s.Name == newName);
    if (existing is not null)
    {
      _rectangularSymbolCache[cacheKey] = existing;
      return existing;
    }

    DB.FamilySymbol? template = symbols.FirstOrDefault(s =>
      FindLengthParameter(s, s_widthParamNames) is not null && FindLengthParameter(s, s_heightParamNames) is not null
    );
    if (template is null)
    {
      return null;
    }

    var newSymbol = (DB.FamilySymbol)template.Duplicate(newName);
    FindLengthParameter(newSymbol, s_widthParamNames)!
      .Set(DB.UnitUtils.ConvertToInternalUnits(widthMm, DB.UnitTypeId.Millimeters));
    FindLengthParameter(newSymbol, s_heightParamNames)!
      .Set(DB.UnitUtils.ConvertToInternalUnits(heightMm, DB.UnitTypeId.Millimeters));

    if (!newSymbol.IsActive)
    {
      newSymbol.Activate();
    }
    _settingsStore.Current.Document.Regenerate();

    symbols.Add(newSymbol);
    _rectangularSymbolCache[cacheKey] = newSymbol;
    return newSymbol;
  }

  /// <summary>
  /// All loaded FamilySymbols in <paramref name="category"/>, as (Family, Type) name pairs - for
  /// populating the Tekla-profile-mapping dialog's family/type picker with what's actually
  /// available in this document (as opposed to a free-text guess).
  /// </summary>
  public IReadOnlyList<(string Family, string Type)> GetFamilySymbolOptions(DB.BuiltInCategory category) =>
    GetSymbols(category).Select(s => (s.FamilyName, s.Name)).ToList();

  /// <summary>
  /// All loaded FamilySymbols in <paramref name="category"/> - for callers (e.g. footing dimension
  /// matching) that need the actual symbol objects, not just their names.
  /// </summary>
  public IReadOnlyList<DB.FamilySymbol> GetFamilySymbols(DB.BuiltInCategory category) => GetSymbols(category);

  /// <summary>
  /// Reads a length-type parameter (type or instance) off <paramref name="element"/> by trying each
  /// name in <paramref name="candidateNames"/> in order, converting to millimeters. Generic
  /// counterpart to the private width/height matching this class already does for rectangular
  /// beam profiles - exposed for callers with their own category-specific candidate name lists
  /// (e.g. footing width/length/thickness, which mean something different than a beam's b/h).
  /// </summary>
  public static bool TryGetParamValueMm(DB.Element element, string[] candidateNames, out double valueMm)
  {
    foreach (string name in candidateNames)
    {
      DB.Parameter? param = element.LookupParameter(name);
      if (param is { StorageType: DB.StorageType.Double, HasValue: true })
      {
        valueMm = DB.UnitUtils.ConvertFromInternalUnits(param.AsDouble(), DB.UnitTypeId.Millimeters);
        return true;
      }
    }
    valueMm = 0;
    return false;
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
  /// Finds the level a given world-space elevation "belongs to" - the highest level at or below
  /// <paramref name="zFeet"/> (Revit internal units), falling back to the lowest level in the
  /// document if <paramref name="zFeet"/> is below every level. Used instead of <see cref="FindLevel"/>
  /// when no level name was captured (always true for Tekla-origin objects, which have no Revit
  /// "Level" concept) - picking the document's lowest level unconditionally, regardless of the
  /// element's actual height, produces a wrong Base Level (and therefore a wrong vertical position)
  /// for anything not already sitting at that lowest level's elevation.
  /// </summary>
  public DB.Level? FindLevelNear(double zFeet)
  {
    List<DB.Level> levels = GetLevels();
    if (levels.Count == 0)
    {
      return null;
    }

    return levels.Where(l => l.Elevation <= zFeet + 1e-6).OrderByDescending(l => l.Elevation).FirstOrDefault()
      ?? levels.OrderBy(l => l.Elevation).First();
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
  /// Finds a basic WallType whose own <see cref="DB.WallType.Width"/> matches
  /// <paramref name="thicknessMm"/> - unlike an isolated-foundation family's thickness (a
  /// family-author-named type parameter that has to be guessed at), <c>WallType.Width</c> is a real
  /// built-in Revit API property, so this is a reliable match with no name-guessing involved. Used
  /// for Tekla-origin walls, which carry a thickness (via their profile string) but no meaningful
  /// WallType name to look up by.
  /// </summary>
  public DB.WallType? FindWallTypeByWidth(double thicknessMm)
  {
    double thicknessFeet = DB.UnitUtils.ConvertToInternalUnits(thicknessMm, DB.UnitTypeId.Millimeters);
    return GetWallTypes()
      .Where(t => t.Kind == DB.WallKind.Basic)
      .FirstOrDefault(t => Math.Abs(t.Width - thicknessFeet) < DB.UnitUtils.ConvertToInternalUnits(DIMENSION_TOLERANCE_MM, DB.UnitTypeId.Millimeters));
  }

  /// <summary>
  /// Finds a non-foundation-slab FloorType whose own compound structure thickness matches
  /// <paramref name="thicknessMm"/> - <see cref="DB.FloorType"/> inherits
  /// <see cref="DB.HostObjAttributes.GetCompoundStructure"/> the same way <see cref="DB.WallType"/>
  /// does, so this is a reliable match with no name-guessing involved, mirroring
  /// <see cref="FindWallTypeByWidth"/>. Used for Tekla-origin floors, which carry a thickness (via
  /// their profile string) but no meaningful FloorType name to look up by.
  /// </summary>
  public DB.FloorType? FindFloorTypeByThickness(double thicknessMm)
  {
    double thicknessFeet = DB.UnitUtils.ConvertToInternalUnits(thicknessMm, DB.UnitTypeId.Millimeters);
    double toleranceFeet = DB.UnitUtils.ConvertToInternalUnits(DIMENSION_TOLERANCE_MM, DB.UnitTypeId.Millimeters);
    return GetFloorTypes()
      .Where(t => !t.IsFoundationSlab)
      .FirstOrDefault(t => Math.Abs(GetCompoundStructureWidth(t) - thicknessFeet) < toleranceFeet);
  }

  private static double GetCompoundStructureWidth(DB.FloorType floorType) =>
    floorType.GetCompoundStructure() is { } structure ? structure.GetWidth() : 0;

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
