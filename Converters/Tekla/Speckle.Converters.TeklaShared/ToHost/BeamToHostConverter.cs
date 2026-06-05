using Speckle.Converters.Common;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.TeklaShared.ToHost;

public class BeamToHostConverter : ITypedConverter<TeklaObject, TSM.Beam>
{
  private readonly ITypedConverter<SOG.Line, TG.LineSegment> _lineConverter;

  public BeamToHostConverter(ITypedConverter<SOG.Line, TG.LineSegment> lineConverter)
  {
    _lineConverter = lineConverter;
  }

  public TSM.Beam Convert(TeklaObject target)
  {
    // Extract location
    if (target.Location is not SOG.Line line)
    {
      throw new ConversionException("Tekla Beam requires a line location.");
    }

    var lineSegment = _lineConverter.Convert(line);

    TSM.Beam beam = new TSM.Beam(lineSegment.Point1, lineSegment.Point2);

    // Set properties from the properties dictionary
    if (target.Properties.TryGetValue("profile", out object? profileObj) && profileObj is string profile)
    {
      beam.Profile.ProfileString = profile;
    }

    if (target.Properties.TryGetValue("material", out object? materialObj) && materialObj is string material)
    {
      beam.Material.MaterialString = material;
    }

    if (target.Name != null)
    {
      beam.Name = target.Name;
    }

    // Apply Native Positioning
    if (target.Properties.TryGetValue("position_depth", out var depth) && depth != null)
    {
      beam.Position.Depth = (TSM.Position.DepthEnum)Enum.Parse(typeof(TSM.Position.DepthEnum), depth.ToString());
    }
    if (target.Properties.TryGetValue("position_depth_offset", out var dOff))
    {
      beam.Position.DepthOffset = System.Convert.ToDouble(dOff);
    }

    if (target.Properties.TryGetValue("position_plane", out var plane) && plane != null)
    {
      beam.Position.Plane = (TSM.Position.PlaneEnum)Enum.Parse(typeof(TSM.Position.PlaneEnum), plane.ToString());
    }
    if (target.Properties.TryGetValue("position_plane_offset", out var pOff) && pOff != null)
    {
      beam.Position.PlaneOffset = System.Convert.ToDouble(pOff!);
    }

    if (target.Properties.TryGetValue("position_rotation", out var rot) && rot != null)
    {
      beam.Position.Rotation = (TSM.Position.RotationEnum)Enum.Parse(typeof(TSM.Position.RotationEnum), rot.ToString());
    }
    if (target.Properties.TryGetValue("position_rotation_offset", out var rOff) && rOff != null)
    {
      beam.Position.RotationOffset = System.Convert.ToDouble(rOff!);
    }

    // Apply End Offsets (Dx, Dy, Dz)
    if (
      target.Properties.TryGetValue(nameof(beam.StartPointOffset), out var sOffObj)
      && sOffObj is System.Collections.IEnumerable sOffList
    )
    {
      var dists = sOffList.Cast<object>().Select(i => System.Convert.ToDouble(i)).ToList();
      beam.StartPointOffset.Dx = dists[0];
      beam.StartPointOffset.Dy = dists[1];
      beam.StartPointOffset.Dz = dists[2];
    }
    if (
      target.Properties.TryGetValue(nameof(beam.EndPointOffset), out var eOffObj)
      && eOffObj is System.Collections.IEnumerable eOffList
    )
    {
      var dists = eOffList.Cast<object>().Select(i => System.Convert.ToDouble(i)).ToList();
      beam.EndPointOffset.Dx = dists[0];
      beam.EndPointOffset.Dy = dists[1];
      beam.EndPointOffset.Dz = dists[2];
    }

    // Apply Phase
    if (target.Properties.TryGetValue("phase", out var phaseNum))
    {
      beam.SetPhase(new TSM.Phase { PhaseNumber = System.Convert.ToInt32(phaseNum) });
    }

    // Apply Numbering Series
    if (target.Properties.TryGetValue("part_prefix", out var pPre) && pPre != null)
    {
      beam.PartNumber.Prefix = pPre.ToString();
    }
    if (target.Properties.TryGetValue("part_start_no", out var pStart))
    {
      beam.PartNumber.StartNumber = System.Convert.ToInt32(pStart);
    }

    if (target.Properties.TryGetValue("assembly_prefix", out var aPre) && aPre != null)
    {
      beam.AssemblyNumber.Prefix = aPre.ToString();
    }
    if (target.Properties.TryGetValue("assembly_start_no", out var aStart))
    {
      beam.AssemblyNumber.StartNumber = System.Convert.ToInt32(aStart);
    }

    beam.Insert();
    return beam;
  }

  public object Convert(object target) => Convert((TeklaObject)target);
}
