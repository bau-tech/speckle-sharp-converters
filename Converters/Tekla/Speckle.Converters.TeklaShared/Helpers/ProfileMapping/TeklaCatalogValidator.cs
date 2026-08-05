using Microsoft.Extensions.Logging;

namespace Speckle.Converters.TeklaShared.Helpers.ProfileMapping;

/// <summary>
/// Checks whether a resolved profile/material string is actually present in Tekla's installed
/// catalog before it's trusted, falling back to a caller-supplied default (with a warning reason)
/// otherwise. Results are cached per operation since many beams/columns in a receive commonly
/// share the same profile/material. Catalog lookups fail open (treated as valid, logged) if the
/// Tekla Open API throws, so a locked/unreachable catalog database never blocks a receive.
/// </summary>
public class TeklaCatalogValidator
{
  private readonly ILogger<TeklaCatalogValidator> _logger;
  private readonly Dictionary<string, bool> _profileCache = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, bool> _materialCache = new(StringComparer.OrdinalIgnoreCase);
  private IReadOnlyList<string>? _catalogProfileNames;
  private IReadOnlyList<string>? _catalogMaterialNames;

  public TeklaCatalogValidator(ILogger<TeklaCatalogValidator> logger)
  {
    _logger = logger;
  }

  // Purely numeric parametric profiles ("500*300", "D400", "PL250") are always insertable in
  // Tekla regardless of catalog content - don't risk a false-negative catalog lookup on them.
  private static readonly System.Text.RegularExpressions.Regex s_numericParametricProfile = new(
    @"^(PL|D)?\d+(\.\d+)?([*xX]\d+(\.\d+)?)*$",
    System.Text.RegularExpressions.RegexOptions.Compiled
  );

  public bool IsValidProfile(string profileString)
  {
    if (s_numericParametricProfile.IsMatch(profileString))
    {
      return true;
    }

    if (_profileCache.TryGetValue(profileString, out bool cached))
    {
      return cached;
    }

    bool isValid = CheckCatalog(
      () => new TSC.LibraryProfileItem().Select(profileString) || new TSC.ParametricProfileItem().Select(profileString),
      profileString,
      "profile"
    );
    _profileCache[profileString] = isValid;
    return isValid;
  }

  public bool IsValidMaterial(string materialName)
  {
    if (_materialCache.TryGetValue(materialName, out bool cached))
    {
      return cached;
    }

    bool isValid = CheckCatalog(() => new TSC.MaterialItem().Select(materialName), materialName, "material");
    _materialCache[materialName] = isValid;
    return isValid;
  }

  /// <summary>
  /// Validates <paramref name="candidate"/> against the Tekla catalog; if it's null or not a real
  /// catalog entry, returns <paramref name="defaultValue"/> along with a human-readable warning.
  /// </summary>
  public (string Value, string? Warning) ValidateOrFallback(string? candidate, string defaultValue, bool isProfile)
  {
    string kind = isProfile ? "profile" : "material";

    if (string.IsNullOrWhiteSpace(candidate))
    {
      return (defaultValue, $"No {kind} was captured; used default {kind} '{defaultValue}'.");
    }

    string nonNullCandidate = candidate!;
    bool isValid = isProfile ? IsValidProfile(nonNullCandidate) : IsValidMaterial(nonNullCandidate);
    if (isValid)
    {
      return (nonNullCandidate, null);
    }

    return (
      defaultValue,
      $"{kind} '{candidate}' is not a valid Tekla catalog entry; used default {kind} '{defaultValue}'."
    );
  }

  /// <summary>
  /// Validates <paramref name="candidates"/> in order and returns the first one present in the
  /// Tekla catalog; if none validate, returns <paramref name="defaultValue"/> with a warning that
  /// names every candidate tried (so the user knows exactly which string to map or fix).
  /// Null/blank candidates are skipped.
  /// </summary>
  public (string Value, string? Warning) ValidateFirstOrFallback(
    IEnumerable<string?> candidates,
    string defaultValue,
    bool isProfile
  )
  {
    string kind = isProfile ? "profile" : "material";
    var tried = new List<string>();

    foreach (var candidate in candidates)
    {
      if (string.IsNullOrWhiteSpace(candidate) || tried.Contains(candidate!))
      {
        continue;
      }

      if (isProfile ? IsValidProfile(candidate!) : IsValidMaterial(candidate!))
      {
        return (candidate!, null);
      }
      tried.Add(candidate!);
    }

    if (tried.Count == 0)
    {
      return (defaultValue, $"No {kind} was captured; used default {kind} '{defaultValue}'.");
    }

    return (
      defaultValue,
      $"{kind} candidate(s) {string.Join(", ", tried.Select(t => $"'{t}'"))} not found in the Tekla catalog; "
        + $"used default {kind} '{defaultValue}'."
    );
  }

  /// <summary>
  /// All library profile names in the installed Tekla catalog, sorted case-insensitively - for
  /// populating selection dropdowns. Cached after the first call; an unreachable catalog yields an
  /// empty list (logged) so callers degrade to free-text input instead of failing.
  /// </summary>
  public IReadOnlyList<string> GetCatalogProfileNames() =>
    _catalogProfileNames ??= EnumerateCatalogNames(
      "profile",
      () =>
      {
        var names = new List<string>();
        var profiles = new TSC.CatalogHandler().GetLibraryProfileItems();
        while (profiles.MoveNext())
        {
          if (profiles.Current is TSC.LibraryProfileItem item && !string.IsNullOrWhiteSpace(item.ProfileName))
          {
            names.Add(item.ProfileName);
          }
        }
        return names;
      }
    );

  /// <summary>
  /// All material names in the installed Tekla catalog, sorted case-insensitively - for populating
  /// selection dropdowns. Same caching/fail-open behavior as <see cref="GetCatalogProfileNames"/>.
  /// </summary>
  public IReadOnlyList<string> GetCatalogMaterialNames() =>
    _catalogMaterialNames ??= EnumerateCatalogNames(
      "material",
      () =>
      {
        var names = new List<string>();
        var materials = new TSC.CatalogHandler().GetMaterialItems();
        while (materials.MoveNext())
        {
          if (materials.Current is { } item && !string.IsNullOrWhiteSpace(item.MaterialName))
          {
            names.Add(item.MaterialName);
          }
        }
        return names;
      }
    );

  private IReadOnlyList<string> EnumerateCatalogNames(string kind, Func<List<string>> enumerate)
  {
    try
    {
      var names = enumerate();
      var deduped = new List<string>(new HashSet<string>(names, StringComparer.OrdinalIgnoreCase));
      deduped.Sort(StringComparer.OrdinalIgnoreCase);
      return deduped;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      _logger.LogWarning(ex, "Could not enumerate the Tekla {Kind} catalog; dropdown suggestions unavailable.", kind);
      return Array.Empty<string>();
    }
  }

  private bool CheckCatalog(Func<bool> select, string value, string kind)
  {
    try
    {
      return select();
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      _logger.LogWarning(
        ex,
        "Tekla catalog lookup failed for {Kind} '{Value}'; treating as valid to avoid blocking receive.",
        kind,
        value
      );
      return true;
    }
  }
}
