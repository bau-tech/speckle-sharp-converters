using System.Collections.Generic;
using System.Linq;
using Speckle.Converters.Common.Objects;
using Speckle.Objects.Data;
using Speckle.Sdk.Common.Exceptions;

namespace Speckle.Converters.TeklaShared.ToHost;

public class RadialGridToHostConverter : ITypedConverter<TeklaObject, TSM.RadialGrid>
{
  public TSM.RadialGrid Convert(TeklaObject target)
  {
    var props = target.properties;
    if (props is null || !props.ContainsKey("radial_coordinates"))
      throw new ConversionException("RadialGrid requires radial_coordinates property.");

    var grid = new TSM.RadialGrid();

    if (target.name is not null)
      grid.Name = target.name;
    else if (props.TryGetValue("name", out var n) && n is not null)
      grid.Name = n.ToString();

    if (props.TryGetValue("radial_coordinates", out var rc) && rc is not null)
      grid.RadialCoordinates = rc.ToString();
    if (props.TryGetValue("angular_coordinates", out var ac) && ac is not null)
      grid.AngularCoordinates = ac.ToString();
    if (props.TryGetValue("coordinate_z", out var cz) && cz is not null)
      grid.CoordinateZ = cz.ToString();
    if (props.TryGetValue("radial_labels", out var rl) && rl is not null)
      grid.RadialLabels = rl.ToString();
    if (props.TryGetValue("angular_labels", out var al) && al is not null)
      grid.AngularLabels = al.ToString();
    if (props.TryGetValue("label_z", out var lz) && lz is not null)
      grid.LabelZ = lz.ToString();

    if (props.TryGetValue("arc_start_extension", out var ase))
      grid.ArcStartExtension = System.Convert.ToDouble(ase);
    if (props.TryGetValue("arc_end_extension", out var aee))
      grid.ArcEndExtension = System.Convert.ToDouble(aee);
    if (props.TryGetValue("angular_lines_start_extension", out var alse))
      grid.AngularLinesStartExtension = System.Convert.ToDouble(alse);
    if (props.TryGetValue("angular_lines_end_extension", out var alee))
      grid.AngularLinesEndExtension = System.Convert.ToDouble(alee);
    if (props.TryGetValue("extension_below_z", out var ebz))
      grid.ExtensionBelowZ = System.Convert.ToDouble(ebz);
    if (props.TryGetValue("extension_above_z", out var eaz))
      grid.ExtensionAboveZ = System.Convert.ToDouble(eaz);

    if (props.TryGetValue("origin", out var origObj) && origObj is IEnumerable<object> origList)
    {
      var o = origList.Select(p => System.Convert.ToDouble(p)).ToList();
      if (o.Count >= 3)
        grid.Origin = new TG.Point(o[0], o[1], o[2]);
    }

    TeklaPartPropertyApplicator.ApplyUdas(grid, props);
    grid.Insert();
    return grid;
  }

  public object Convert(object target) => Convert((TeklaObject)target);
}
