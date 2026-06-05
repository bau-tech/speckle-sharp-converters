using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Speckle.Converters.TeklaShared.Extensions;

namespace Speckle.Converters.TeklaShared.ToSpeckle.Helpers;

public class ClassPropertyExtractor
{
  public ClassPropertyExtractor() { }

  public Dictionary<string, object?> GetProperties(TSM.ModelObject modelObject)
  {
    Dictionary<string, object?> properties = new();

    switch (modelObject)
    {
      case TSM.Part part:
        AddPartProperties(part, properties);
        break;
      case TSM.BoltGroup boltGroup:
        AddBoltGroupProperties(boltGroup, properties);
        break;
      case TSM.SingleRebar singleRebar:
        AddSingleRebarProperties(singleRebar, properties);
        break;
      case TSM.RebarMesh rebarMesh:
        AddRebarMeshProperties(rebarMesh, properties);
        break;
      case TSM.RebarGroup rebarGroup:
        AddRebarGroupProperties(rebarGroup, properties);
        break;
      case TSM.Fitting fitting:
        AddFittingProperties(fitting, properties);
        break;
      case TSM.BooleanPart booleanPart:
        AddBooleanPartProperties(booleanPart, properties);
        break;
      case TSM.Weld weld:
        AddWeldProperties(weld, properties);
        break;
    }

    return properties;
  }

  private void AddPartProperties(TSM.Part part, Dictionary<string, object?> properties)
  {
    properties["profile"] = part.Profile.ProfileString;
    properties["material"] = part.Material.MaterialString;
    properties["class"] = part.Class;
    properties["finish"] = part.Finish;

    var assembly = part.GetAssembly();
    if (assembly != null)
    {
      properties["assembly_id"] = assembly.Identifier.GUID.ToString();
      // Use GetMainPart() method instead of property if property is missing or for better compatibility
      var mainPart = assembly.GetMainPart();
      if (mainPart != null)
      {
        properties["is_main_part"] = mainPart.Identifier == part.Identifier;
      }
    }

    properties["part_prefix"] = part.PartNumber.Prefix;
    properties["part_start_no"] = part.PartNumber.StartNumber;
    properties["assembly_prefix"] = part.AssemblyNumber.Prefix;
    properties["assembly_start_no"] = part.AssemblyNumber.StartNumber;

    TSM.Phase partPhase;
    if (part.GetPhase(out partPhase))
    {
      properties["phase"] = partPhase.PhaseNumber;
      properties["phase_name"] = partPhase.PhaseName;
    }

    properties["position_depth"] = part.Position.Depth.ToString();
    properties["position_depth_offset"] = part.Position.DepthOffset;
    properties["position_plane"] = part.Position.Plane.ToString();
    properties["position_plane_offset"] = part.Position.PlaneOffset;
    properties["position_rotation"] = part.Position.Rotation.ToString();
    properties["position_rotation_offset"] = part.Position.RotationOffset;

    // Only Beam has StartPointOffset/EndPointOffset
    if (part is TSM.Beam beam)
    {
      properties[nameof(beam.StartPointOffset)] = new List<double>
      {
        beam.StartPointOffset.Dx,
        beam.StartPointOffset.Dy,
        beam.StartPointOffset.Dz
      };
      properties[nameof(beam.EndPointOffset)] = new List<double>
      {
        beam.EndPointOffset.Dx,
        beam.EndPointOffset.Dy,
        beam.EndPointOffset.Dz
      };
    }
  }

  private void AddBoltGroupProperties(TSM.BoltGroup boltGroup, Dictionary<string, object?> properties)
  {
    properties["boltSize"] = boltGroup.BoltSize;
    properties["boltStandard"] = boltGroup.BoltStandard;
    properties["boltType"] = boltGroup.BoltType.ToString();
    properties["cutLength"] = boltGroup.CutLength;
    properties["extraLength"] = boltGroup.ExtraLength;
    properties["threadInMaterial"] = boltGroup.ThreadInMaterial.ToString();
    properties["tolerance"] = boltGroup.Tolerance;

    properties["holeType"] = boltGroup.HoleType.ToString();
    properties["slottedHoleX"] = boltGroup.SlottedHoleX;
    properties["slottedHoleY"] = boltGroup.SlottedHoleY;
    properties["rotateSlots"] = boltGroup.RotateSlots.ToString();

    dynamic dynBolt = boltGroup;
    switch (boltGroup)
    {
      case TSM.BoltArray array:
        properties["patternType"] = "Array";
        try
        {
          properties["xDistances"] = ExtractDistances(dynBolt.XDistance);
        }
        catch { }
        try
        {
          properties["yDistances"] = ExtractDistances(dynBolt.YDistance);
        }
        catch { }
        properties["startPoint"] = ExtractPoint(array.FirstPosition);
        properties["endPoint"] = ExtractPoint(array.SecondPosition);
        break;
      case TSM.BoltCircle circle:
        properties["patternType"] = "Circle";
        properties["boltCount"] = circle.NumberOfBolts;
        properties["diameter"] = circle.Diameter;
        properties["centerPoint"] = ExtractPoint(circle.FirstPosition);
        properties["directionPoint"] = ExtractPoint(circle.SecondPosition);
        break;
      default:
        if (boltGroup.GetType().Name.Contains("BoltXY"))
        {
          properties["patternType"] = "XY";
          try
          {
            properties["xCoordinates"] = ExtractDistances(dynBolt.XCoordinate);
          }
          catch { }
          try
          {
            properties["yCoordinates"] = ExtractDistances(dynBolt.YCoordinate);
          }
          catch { }
          properties["startPoint"] = ExtractPoint(boltGroup.FirstPosition);
          properties["endPoint"] = ExtractPoint(boltGroup.SecondPosition);
        }
        break;
    }

    var secondaryPartIds = new List<string>();
    foreach (TSM.Part p in boltGroup.OtherPartsToBolt)
    {
      secondaryPartIds.Add(p.GetSpeckleApplicationId());
    }
    properties["secondaryPartIds"] = secondaryPartIds;
  }

  private List<double> ExtractDistances(IEnumerable distances)
  {
    var list = new List<double>();
    if (distances != null)
    {
      foreach (var d in distances)
      {
        list.Add(System.Convert.ToDouble(d));
      }
    }
    return list;
  }

  private List<double> ExtractPoint(TG.Point pt) => new() { pt.X, pt.Y, pt.Z };

  private void AddSingleRebarProperties(TSM.SingleRebar singleRebar, Dictionary<string, object?> properties)
  {
    properties["grade"] = singleRebar.Grade;
    properties["size"] = singleRebar.Size;
    properties["class"] = singleRebar.Class;
    if (singleRebar.Father != null)
    {
      properties["father_id"] = singleRebar.Father.GetSpeckleApplicationId();
    }
  }

  private void AddRebarMeshProperties(TSM.RebarMesh rebarMesh, Dictionary<string, object?> properties)
  {
    properties["grade"] = rebarMesh.Grade;
  }

  private void AddRebarGroupProperties(TSM.RebarGroup rebarGroup, Dictionary<string, object?> properties)
  {
    properties["grade"] = rebarGroup.Grade;
    properties["size"] = rebarGroup.Size;
    properties["class"] = rebarGroup.Class;
    properties["start_hook_type"] = rebarGroup.StartHook.Shape.ToString();
    properties["end_hook_type"] = rebarGroup.EndHook.Shape.ToString();

    if (rebarGroup.Father != null)
    {
      properties["father_id"] = rebarGroup.Father.GetSpeckleApplicationId();
    }
  }

  private void AddFittingProperties(TSM.Fitting fitting, Dictionary<string, object?> properties)
  {
    properties["plane_origin"] = new List<double>
    {
      fitting.Plane.Origin.X,
      fitting.Plane.Origin.Y,
      fitting.Plane.Origin.Z
    };
    properties["plane_x"] = new List<double> { fitting.Plane.AxisX.X, fitting.Plane.AxisX.Y, fitting.Plane.AxisX.Z };
    properties["plane_y"] = new List<double> { fitting.Plane.AxisY.X, fitting.Plane.AxisY.Y, fitting.Plane.AxisY.Z };
  }

  private void AddBooleanPartProperties(TSM.BooleanPart booleanPart, Dictionary<string, object?> properties)
  {
    properties["type"] = booleanPart.Type.ToString();
    if (booleanPart.OperativePart is TSM.Part opPart)
    {
      properties["operative_type"] = opPart.GetType().Name;
      properties["operative_profile"] = opPart.Profile.ProfileString;
      properties["operative_material"] = opPart.Material.MaterialString;
      properties["operative_class"] = opPart.Class;
    }
  }

  private void AddWeldProperties(TSM.Weld weld, Dictionary<string, object?> properties)
  {
    properties["size_above"] = weld.SizeAbove;
    properties["size_below"] = weld.SizeBelow;
    properties["type_above"] = weld.TypeAbove.ToString();
    properties["type_below"] = weld.TypeBelow.ToString();
    properties["shop_site"] = weld.ShopWeld.ToString();
    properties["around"] = weld.AroundWeld.ToString();

    if (weld.MainObject != null)
    {
      properties["main_id"] = weld.MainObject.GetSpeckleApplicationId();
    }
    if (weld.SecondaryObject != null)
    {
      properties["secondary_id"] = weld.SecondaryObject.GetSpeckleApplicationId();
    }
  }
}
