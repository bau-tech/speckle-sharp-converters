using Speckle.Converters.Common.Objects;
using Speckle.Objects.Data;
using Speckle.Sdk.Common.Exceptions;

namespace Speckle.Converters.TeklaShared.ToHost;

public class GridToHostConverter : ITypedConverter<TeklaObject, TSM.Grid>
{
  public TSM.Grid Convert(TeklaObject target)
  {
    var props = target.properties;
    if (props is null || !props.ContainsKey("coordinate_x"))
      throw new ConversionException("Grid requires coordinate_x property.");

    var grid = new TSM.Grid();

    if (target.name is not null)
      grid.Name = target.name;
    else if (props.TryGetValue("name", out var n) && n is not null)
      grid.Name = n.ToString();

    if (props.TryGetValue("coordinate_x", out var cx) && cx is not null)
      grid.CoordinateX = cx.ToString();
    if (props.TryGetValue("coordinate_y", out var cy) && cy is not null)
      grid.CoordinateY = cy.ToString();
    if (props.TryGetValue("coordinate_z", out var cz) && cz is not null)
      grid.CoordinateZ = cz.ToString();

    if (props.TryGetValue("label_x", out var lx) && lx is not null)
      grid.LabelX = lx.ToString();
    if (props.TryGetValue("label_y", out var ly) && ly is not null)
      grid.LabelY = ly.ToString();
    if (props.TryGetValue("label_z", out var lz) && lz is not null)
      grid.LabelZ = lz.ToString();

    if (props.TryGetValue("origin", out var origObj) && origObj is IEnumerable<object> origList)
    {
      var o = origList.Select(p => System.Convert.ToDouble(p)).ToList();
      if (o.Count >= 3)
        grid.Origin = new TG.Point(o[0], o[1], o[2]);
    }
    if (props.TryGetValue("extension_left_x", out var elx))
      grid.ExtensionLeftX = System.Convert.ToDouble(elx);
    if (props.TryGetValue("extension_right_x", out var erx))
      grid.ExtensionRightX = System.Convert.ToDouble(erx);
    if (props.TryGetValue("extension_left_y", out var ely))
      grid.ExtensionLeftY = System.Convert.ToDouble(ely);
    if (props.TryGetValue("extension_right_y", out var ery))
      grid.ExtensionRightY = System.Convert.ToDouble(ery);

    TeklaPartPropertyApplicator.ApplyUdas(grid, props);

    grid.Insert();
    return grid;
  }

  public object Convert(object target) => Convert((TeklaObject)target);
}
