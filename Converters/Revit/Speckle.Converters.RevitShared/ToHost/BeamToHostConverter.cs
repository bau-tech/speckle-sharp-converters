using Speckle.Converters.Common.Objects;
using Speckle.Sdk.Models;

namespace Speckle.Converters.RevitShared.ToHost;

public class BeamToHostConverter : ITypedConverter<Base, DB.Element>
{
  private readonly StructuralFramingHelper _structuralFramingHelper;

  public BeamToHostConverter(StructuralFramingHelper structuralFramingHelper)
  {
    _structuralFramingHelper = structuralFramingHelper;
  }

  public DB.Element Convert(Base target) =>
    _structuralFramingHelper.Create(target, DB.BuiltInCategory.OST_StructuralFraming, DB.Structure.StructuralType.Beam);

  public object Convert(object target) => Convert((Base)target);
}
