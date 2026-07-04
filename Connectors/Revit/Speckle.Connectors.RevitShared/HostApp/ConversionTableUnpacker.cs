using Autodesk.Revit.DB;
using Microsoft.Extensions.Logging;
using Speckle.Connectors.Common.Operations;
using Speckle.Sdk;

namespace Speckle.Connectors.Revit.HostApp;

/// <summary>
/// Builds the <see cref="ConversionTable"/> inventory (distinct structural family/types and
/// structural materials, with cheap dimensional hints) that is attached to the root object on
/// send so receiving connectors can present a mapping UI before baking. Best-effort only:
/// a bad element is skipped and never fails the send.
/// </summary>
public class ConversionTableUnpacker
{
  // categories with native ToHost converters on the Tekla side
  private static readonly HashSet<BuiltInCategory> s_structuralCategories = new()
  {
    BuiltInCategory.OST_StructuralFraming,
    BuiltInCategory.OST_StructuralColumns,
    BuiltInCategory.OST_StructuralFoundation,
    BuiltInCategory.OST_Floors,
    BuiltInCategory.OST_Walls,
  };

  // type parameters that commonly carry section dimensions; values are internal units (feet)
  private static readonly string[] s_widthParameterNames = { "b", "bf", "Width", "Breite" };
  private static readonly string[] s_heightParameterNames = { "h", "d", "Height", "Depth", "Höhe" };

  private readonly ILogger<ConversionTableUnpacker> _logger;

  public ConversionTableUnpacker(ILogger<ConversionTableUnpacker> logger)
  {
    _logger = logger;
  }

  public ConversionTable Unpack(IReadOnlyList<Element> elements)
  {
    var table = new ConversionTable { SourceApplication = "Revit" };
    var seenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var seenMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var element in elements)
    {
      try
      {
        var builtInCategory = element.Category?.BuiltInCategory;
        if (builtInCategory is null || !s_structuralCategories.Contains(builtInCategory.Value))
        {
          continue;
        }

        // always resolve through the element's own document - it may live in a linked model
        var doc = element.Document;
        var elementType = doc.GetElement(element.GetTypeId()) as ElementType;

        if (elementType is not null)
        {
          string family = elementType.FamilyName ?? "";
          string type = elementType.Name ?? "";
          if (type.Length > 0 && seenTypes.Add($"{family}|{type}"))
          {
            var entry = new ConversionTableProfileEntry
            {
              Category = element.Category?.Name ?? "",
              Family = family,
              Type = type,
            };
            entry.WidthMm = TryGetLengthMm(elementType, s_widthParameterNames);
            entry.HeightMm = TryGetLengthMm(elementType, s_heightParameterNames);
            table.Profiles.Add(entry);
          }
        }

        string? materialName = TryGetStructuralMaterialName(element, elementType);
        if (!string.IsNullOrWhiteSpace(materialName) && seenMaterials.Add(materialName!))
        {
          table.Materials.Add(new ConversionTableMaterialEntry { Name = materialName! });
        }
      }
      catch (Exception ex) when (!ex.IsFatal())
      {
        _logger.LogDebug(ex, "Skipped element {UniqueId} while building the conversion table.", element.UniqueId);
      }
    }

    return table;
  }

  private static string? TryGetStructuralMaterialName(Element element, ElementType? elementType)
  {
    var param = element.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
    if (param is null || param.StorageType != StorageType.ElementId)
    {
      param = elementType?.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
    }
    if (param is null || param.StorageType != StorageType.ElementId)
    {
      return null;
    }

    var materialId = param.AsElementId();
    if (materialId == ElementId.InvalidElementId)
    {
      return null;
    }

    return (element.Document.GetElement(materialId) as Material)?.Name;
  }

  private static double? TryGetLengthMm(ElementType elementType, string[] parameterNames)
  {
    foreach (string name in parameterNames)
    {
      var param = elementType.LookupParameter(name);
      if (param is null || param.StorageType != StorageType.Double || !param.HasValue)
      {
        continue;
      }
      double internalValue = param.AsDouble();
      if (internalValue <= 0)
      {
        continue;
      }
      return UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.Millimeters);
    }
    return null;
  }
}
