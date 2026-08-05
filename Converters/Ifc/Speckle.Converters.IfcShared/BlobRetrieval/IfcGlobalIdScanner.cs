using Microsoft.Extensions.Logging;
using Speckle.Converters.IfcShared.StepParsing;
using Speckle.InterfaceGenerator;

namespace Speckle.Converters.IfcShared.BlobRetrieval;

/// <summary>
/// Phase 1 scan: builds a lookup of every IFC <c>GlobalId</c> present in a downloaded <c>.ifc</c>
/// file, without needing any typed IFC schema layer. <c>GlobalId</c> is always the first attribute
/// on any <c>IfcRoot</c>-derived entity (walls, columns, beams, slabs, openings, the project/site/
/// building/storey hierarchy, etc.) - confirmed across every entity type sampled in this feature's
/// live-file investigation. This is deliberately just a correlation-rate check: it says nothing
/// about axis/profile/body data, only "does this file's element identity line up with what's
/// already in Speckle" (<c>DataObject.applicationId</c>, set by the production IFC importer to the
/// same <c>GlobalId</c>).
/// </summary>
/// <remarks>
/// Not every entity's first attribute is a string (e.g. <c>IfcCartesianPoint</c> starts with a
/// coordinate list, <c>IfcCircleProfileDef</c> starts with an area-type symbol) - those are silently
/// skipped by the type check below, no allow-list needed. A small number of non-<c>IfcRoot</c>
/// entities DO have a leading string that isn't a <c>GlobalId</c> (e.g. <c>IfcMaterial</c>'s name) -
/// filtered out by the length/character-class check, since a real IFC <c>GlobalId</c> is always
/// exactly 22 characters from IFC's base64-like alphabet, which a material name never matches.
/// </remarks>
[GenerateAutoInterface]
public class IfcGlobalIdScanner(ILogger<IfcGlobalIdScanner> logger) : IIfcGlobalIdScanner
{
  private const int GLOBAL_ID_LENGTH = 22;

  /// <summary>
  /// Scans <paramref name="ifcFilePath"/> and returns every <c>GlobalId</c> found, mapped to its
  /// STEP express id (<c>#123</c> - useful for Phase 2+'s typed extraction, which resolves entities
  /// by express id via <see cref="StepGraph"/>).
  /// </summary>
  public IReadOnlyDictionary<string, uint> ScanGlobalIds(string ifcFilePath)
  {
    var result = new Dictionary<string, uint>(StringComparer.Ordinal);

    using var doc = new StepDocument(ifcFilePath);

    foreach (var raw in doc.RawInstances)
    {
      // RawInstances is sized to LineOffsets.Count, which can exceed NumRawInstances - trailing
      // slots are default/invalid and must be skipped (mirrors the original code's own usage
      // pattern, e.g. the removed IfcGraph's instance walk).
      if (!raw.IsValid())
      {
        continue;
      }

      var instance = doc.GetInstanceWithData(raw);
      if (instance.Count == 0)
      {
        continue;
      }

      if (instance[0] is not StepString candidate)
      {
        continue;
      }

      // ByteSpan.ToString() copies into a managed string - safe to keep after `doc` is disposed.
      string value = candidate.Value.ToString();
      if (!IsPlausibleGlobalId(value))
      {
        continue;
      }

      result[value] = instance.Id;
    }

    logger.LogInformation(
      "IfcGlobalIdScanner: found {Count} candidate GlobalIds in {FilePath}.",
      result.Count,
      ifcFilePath
    );

    return result;
  }

  private static bool IsPlausibleGlobalId(string value)
  {
    if (value.Length != GLOBAL_ID_LENGTH)
    {
      return false;
    }

    foreach (char c in value)
    {
      bool isAllowed =
        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_' || c == '$';
      if (!isAllowed)
      {
        return false;
      }
    }

    return true;
  }
}
