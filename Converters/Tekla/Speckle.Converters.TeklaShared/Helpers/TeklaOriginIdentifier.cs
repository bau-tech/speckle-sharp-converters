using System.Diagnostics.CodeAnalysis;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Speckle.Converters.TeklaShared.Helpers;

/// <summary>
/// Persists the ORIGIN Speckle applicationId (the id a part carried the first time it was ever
/// received/authored - e.g. a Revit-authored element's UniqueId, or a Tekla part's own GUID) onto a
/// Tekla part via a User-Defined Attribute, so a later receive can recognize "this incoming object is
/// the same real-world part I already have" and update it in place instead of inserting a duplicate.
/// </summary>
public static class TeklaOriginIdentifier
{
  private const string UDA_NAME = "SpeckleOriginApplicationId";
  private const string INP_FILE_NAME = "objects_speckle.inp";
  private static bool s_schemaEnsured;

  // Mirrors Tekla's own documented "make a UDA unique" example (Trimble User Assistance ->
  // "Example: Create and update a user-defined attribute (UDA)"), substituting our UDA name/tab.
  // unique_attribute (vs. attribute) is the actual mechanism native Tekla "Copy" consults to decide
  // whether to duplicate a value onto a copy - the Tekla.Structures.Catalogs.UserPropertyItem.Unique
  // API property that was tried first does NOT affect native Copy behavior (confirmed by testing: a
  // copied part still carried the duplicated origin id), so this file is the real fix.
  private const string INP_CONTENT =
    "part(0,\"Part\")\r\n"
    + "{\r\n"
    + " tab_page(\"Speckle\")\r\n"
    + " {\r\n"
    + " unique_attribute(\"SpeckleOriginApplicationId\", \"Speckle Origin Application Id\", string,\"%s\", no, none, \"0,0\", \"0,0\")\r\n"
    + " {\r\n"
    + " value(\"\", 0)\r\n"
    + " }\r\n"
    + " }\r\n"
    + " tab_page(\"Speckle\", \"Speckle\", 19)\r\n"
    + " modify (1)\r\n"
    + "}\r\n"
    + "column(0,\"j_column\")\r\n"
    + "{\r\n"
    + " tab_page(\"Speckle\", \"Speckle\", 19)\r\n"
    + " modify (1)\r\n"
    + "}\r\n";

  public static void Set(TSM.ModelObject part, string originApplicationId, ILogger? logger = null)
  {
    EnsureNonCopyableSchemaFile(logger);
    part.SetUserProperty(UDA_NAME, originApplicationId);
  }

  public static bool TryGet(TSM.ModelObject part, [NotNullWhen(true)] out string? originApplicationId)
  {
    string value = string.Empty;
    if (part.GetUserProperty(UDA_NAME, ref value) && !string.IsNullOrEmpty(value))
    {
      originApplicationId = value;
      return true;
    }

    originApplicationId = null;
    return false;
  }

  /// <summary>
  /// Writes an objects.inp file (suffixed so it merges alongside any pre-existing objects.inp in the
  /// same folder rather than replacing it) into the current model's own folder, declaring
  /// <see cref="UDA_NAME"/> as a non-copyable <c>unique_attribute</c>. Model-folder objects.inp files
  /// take the highest precedence in Tekla's search order. Idempotent (only writes if missing/stale)
  /// and fails open - logged, never throws - so a locked/read-only model folder never blocks a receive.
  /// Only takes effect for the CURRENTLY open model after running
  /// "File -> Diagnose & repair -> Diagnose and change attribute definitions" (or reopening the
  /// model); <see cref="TeklaOutgoingApplicationIdResolver"/> stays in place as a safety net for
  /// parts copied before that refresh happens, or on models where this write fails.
  /// </summary>
  private static void EnsureNonCopyableSchemaFile(ILogger? logger)
  {
    if (s_schemaEnsured)
    {
      return;
    }

    try
    {
      string? modelPath = new TSM.Model().GetInfo()?.ModelPath;
      if (string.IsNullOrEmpty(modelPath))
      {
        return;
      }

      string inpPath = Path.Combine(modelPath, INP_FILE_NAME);
      if (!File.Exists(inpPath) || File.ReadAllText(inpPath) != INP_CONTENT)
      {
        File.WriteAllText(inpPath, INP_CONTENT);
      }

      s_schemaEnsured = true;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      logger?.LogWarning(
        ex,
        "Could not write {InpFile} for the non-copyable {UdaName} UDA definition.",
        INP_FILE_NAME,
        UDA_NAME
      );
    }
  }
}
