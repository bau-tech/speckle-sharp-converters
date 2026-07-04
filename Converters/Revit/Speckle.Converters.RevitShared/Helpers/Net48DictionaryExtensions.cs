namespace Speckle.Converters.RevitShared.Helpers;

/// <summary>
/// Polyfills for BCL members that only exist on netstandard2.1+/netcoreapp2.0+ (e.g. <c>Dictionary.GetValueOrDefault</c>
/// and <c>string.Contains(string, StringComparison)</c>), which are unavailable on this project's net48-targeting
/// Revit versions (2023/2024) but compile fine on the net8-targeting ones - so a raw call site works locally
/// against a recent Revit version and only breaks the older ones at CI/build time.
/// </summary>
public static class Net48DictionaryExtensions
{
  public static TValue? GetOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key)
    where TKey : notnull => dict.TryGetValue(key, out var value) ? value : default;

  public static TValue GetOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key, TValue defaultValue)
    where TKey : notnull => dict.TryGetValue(key, out var value) ? value : defaultValue;

  // CA2249 wants string.Contains(string, StringComparison) here, but that overload is exactly
  // what's unavailable on net48 - this method exists to work around that.
#pragma warning disable CA2249
  public static bool ContainsOrdinalIgnoreCase(this string source, string value) =>
    source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
#pragma warning restore CA2249
}
