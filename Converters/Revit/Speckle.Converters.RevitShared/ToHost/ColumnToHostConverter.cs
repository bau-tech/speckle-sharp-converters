using Speckle.Converters.Common.Objects;
using Speckle.Converters.RevitShared.Helpers;
using Speckle.Sdk.Models;

namespace Speckle.Converters.RevitShared.ToHost;

/// <summary>
/// Reconstructs structural columns (<c>OST_StructuralColumns</c>) as native Revit FamilyInstances.
/// Architectural columns are out of scope.
/// </summary>
public class ColumnToHostConverter : ITypedConverter<Base, DB.Element>
{
  private readonly StructuralFramingHelper _structuralFramingHelper;
  private readonly RevitElementTypeResolver _typeResolver;

  public ColumnToHostConverter(StructuralFramingHelper structuralFramingHelper, RevitElementTypeResolver typeResolver)
  {
    _structuralFramingHelper = structuralFramingHelper;
    _typeResolver = typeResolver;
  }

  public DB.Element Convert(Base target)
  {
    DB.FamilyInstance instance = _structuralFramingHelper.Create(
      target,
      DB.BuiltInCategory.OST_StructuralColumns,
      DB.Structure.StructuralType.Column
    );

    ApplyVerticalExtents(target, instance);
    _structuralFramingHelper.ReapplyPlacementRotation(target, instance);

    return instance;
  }

  /// <summary>
  /// Applies the captured base/top level constraints and offsets so the received column spans the same
  /// vertical extent as the source, instead of defaulting to the symbol's unconnected height at the base level.
  /// </summary>
  private void ApplyVerticalExtents(Base target, DB.FamilyInstance column)
  {
    if (
      RevitElementPropertyApplicator.TryGetLengthInFeet(
        target,
        "Instance Parameters",
        "FAMILY_BASE_LEVEL_OFFSET_PARAM",
        out double baseOffset
      )
    )
    {
      RevitElementPropertyApplicator.TrySetDouble(column, DB.BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM, baseOffset);
    }

    if (
      RevitElementPropertyApplicator.TryGetLevelReferenceName(
        target,
        "Instance Parameters",
        "FAMILY_TOP_LEVEL_PARAM",
        out string? topLevelName
      )
      && topLevelName is not null
      && _typeResolver.FindLevel(topLevelName) is DB.Level topLevel
    )
    {
      RevitElementPropertyApplicator.TrySetElementId(column, DB.BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, topLevel.Id);
    }

    if (
      RevitElementPropertyApplicator.TryGetLengthInFeet(
        target,
        "Instance Parameters",
        "FAMILY_TOP_LEVEL_OFFSET_PARAM",
        out double topOffset
      )
    )
    {
      RevitElementPropertyApplicator.TrySetDouble(column, DB.BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, topOffset);
    }
  }

  public object Convert(object target) => Convert((Base)target);
}
