using Speckle.Converters.RevitShared.Helpers;
using Speckle.Sdk.Models;

namespace Speckle.Converters.RevitShared.ToHost;

/// <summary>
/// Best-effort application of captured Revit instance parameters back onto natively-created elements.
/// Lookups are by <c>BuiltInParameter</c> name (stable, locale-independent), searching across all parameter
/// groups. Each setter is independent and non-fatal: a missing parameter, unrecognized unit, or read-only
/// target is silently skipped, leaving the element with its Revit-assigned defaults.
/// </summary>
public static class RevitElementPropertyApplicator
{
  /// <summary>
  /// Looks up a captured parameter dict by its Revit <c>BuiltInParameter</c> name (e.g. "WALL_USER_HEIGHT_PARAM")
  /// within <c>properties["Parameters"][bucket][*][internalDefinitionName]</c>, searching across all groups
  /// (the group a parameter lands in isn't predictable/stable).
  /// </summary>
  public static bool TryGetParameter(
    Base source,
    string bucket,
    string internalDefinitionName,
    out Dictionary<string, object>? parameter
  )
  {
    parameter = null;

    if (
      source["properties"] is not Dictionary<string, object?> properties
      || properties.GetOrDefault("Parameters") is not Dictionary<string, object> buckets
      || buckets.GetOrDefault(bucket) is not Dictionary<string, object> groups
    )
    {
      return false;
    }

    foreach (object groupObj in groups.Values)
    {
      if (groupObj is not Dictionary<string, object> group)
      {
        continue;
      }

      foreach (object paramObj in group.Values)
      {
        if (
          paramObj is Dictionary<string, object> param
          && param.GetOrDefault("internalDefinitionName") as string == internalDefinitionName
        )
        {
          parameter = param;
          return true;
        }
      }
    }

    return false;
  }

  /// <summary>
  /// Reads a captured Double-storage length parameter and converts it to internal feet using its captured
  /// unit type id. Returns false if the parameter is missing, has no value, or no unit type id was captured.
  /// </summary>
  public static bool TryGetLengthInFeet(Base source, string bucket, string internalDefinitionName, out double feet)
  {
    feet = 0;

    if (
      !TryGetParameter(source, bucket, internalDefinitionName, out Dictionary<string, object>? parameter)
      || !TryToDouble(parameter!.GetOrDefault("value"), out double value)
      || parameter!.GetOrDefault("unitsTypeId") is not string unitsTypeId
    )
    {
      return false;
    }

    feet = DB.UnitUtils.ConvertToInternalUnits(value, new DB.ForgeTypeId(unitsTypeId));
    return true;
  }

  /// <summary>
  /// Reads a captured Double-storage angle parameter and converts it to internal radians using its captured
  /// unit type id. Returns false if the parameter is missing, has no value, or no unit type id was captured.
  /// </summary>
  public static bool TryGetAngleInRadians(Base source, string bucket, string internalDefinitionName, out double radians)
  {
    radians = 0;

    if (
      !TryGetParameter(source, bucket, internalDefinitionName, out Dictionary<string, object>? parameter)
      || !TryToDouble(parameter!.GetOrDefault("value"), out double value)
      || parameter!.GetOrDefault("unitsTypeId") is not string unitsTypeId
    )
    {
      return false;
    }

    radians = DB.UnitUtils.ConvertToInternalUnits(value, new DB.ForgeTypeId(unitsTypeId));
    return true;
  }

  /// <summary>
  /// Reads a captured ElementId-storage parameter that resolves to a Level reference (e.g. "Top Constraint"),
  /// returning the referenced Level's name. Returns false if the parameter wasn't captured at all; returns
  /// true with a null name if it was captured but is "Unconnected" (no level reference).
  /// </summary>
  public static bool TryGetLevelReferenceName(
    Base source,
    string bucket,
    string internalDefinitionName,
    out string? levelName
  )
  {
    levelName = null;

    if (!TryGetParameter(source, bucket, internalDefinitionName, out Dictionary<string, object>? parameter))
    {
      return false;
    }

    levelName = parameter!.GetOrDefault("value") as string;
    return true;
  }

  /// <summary>Sets a writable Double-storage parameter, no-op if the parameter is missing or read-only.</summary>
  public static void TrySetDouble(DB.Element element, DB.BuiltInParameter builtInParameter, double value)
  {
    DB.Parameter? parameter = element.get_Parameter(builtInParameter);
    if (parameter is { IsReadOnly: false })
    {
      parameter.Set(value);
    }
  }

  /// <summary>Sets a writable ElementId-storage parameter, no-op if the parameter is missing or read-only.</summary>
  public static void TrySetElementId(DB.Element element, DB.BuiltInParameter builtInParameter, DB.ElementId value)
  {
    DB.Parameter? parameter = element.get_Parameter(builtInParameter);
    if (parameter is { IsReadOnly: false })
    {
      parameter.Set(value);
    }
  }

  /// <summary>Converts a deserialized numeric dynamic property value to double.</summary>
  public static bool TryToDouble(object? value, out double result)
  {
    switch (value)
    {
      case double d:
        result = d;
        return true;
      case float f:
        result = f;
        return true;
      case decimal m:
        result = (double)m;
        return true;
      case long l:
        result = l;
        return true;
      case int i:
        result = i;
        return true;
      case short s:
        result = s;
        return true;
      default:
        result = 0;
        return false;
    }
  }
}
