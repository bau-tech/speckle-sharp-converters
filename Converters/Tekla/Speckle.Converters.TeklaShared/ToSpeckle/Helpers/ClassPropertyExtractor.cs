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
      // Subtypes of Part must come before the Part case
      case TSM.BentPlate bentPlate:
        AddPartProperties(bentPlate, properties);
        AddBentPlateProperties(bentPlate, properties);
        break;
      case TSM.SpiralBeam spiralBeam:
        AddPartProperties(spiralBeam, properties);
        AddSpiralBeamProperties(spiralBeam, properties);
        break;
      case TSM.LoftedPlate loftedPlate:
        AddPartProperties(loftedPlate, properties);
        AddLoftedPlateProperties(loftedPlate, properties);
        break;
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
      case TSM.RebarSet rebarSet:
        AddRebarSetProperties(rebarSet, properties);
        break;
      case TSM.Fitting fitting:
        AddFittingProperties(fitting, properties);
        break;
      case TSM.CutPlane cutPlane:
        AddCutPlaneProperties(cutPlane, properties);
        break;
      case TSM.EdgeChamfer edgeChamfer:
        AddEdgeChamferProperties(edgeChamfer, properties);
        break;
      case TSM.BooleanPart booleanPart:
        AddBooleanPartProperties(booleanPart, properties);
        break;
      case TSM.Weld weld:
        AddWeldProperties(weld, properties);
        break;
      case TSM.RadialGrid radialGrid:
        AddRadialGridProperties(radialGrid, properties);
        break;
      case TSM.Grid grid:
        AddGridProperties(grid, properties);
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

    // Beam-specific properties
    if (part is TSM.Beam beam)
    {
      properties[nameof(beam.StartPointOffset)] = new List<double>
      {
        beam.StartPointOffset.Dx,
        beam.StartPointOffset.Dy,
        beam.StartPointOffset.Dz,
      };
      properties[nameof(beam.EndPointOffset)] = new List<double>
      {
        beam.EndPointOffset.Dx,
        beam.EndPointOffset.Dy,
        beam.EndPointOffset.Dz,
      };

      // Note: Prelength/Twist/Camber deformation properties are Tekla-API-version-specific
      // and require confirmation of the correct property names before enabling here.
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

    // Position/Offset are base-class TSM.BoltGroup properties present on every shape — without
    // them the receiver falls back to Tekla's defaults (MIDDLE/MIDDLE/FRONT, zero offsets), which
    // can place the bolt group flush against the part faces instead of at the originally modeled
    // depth/plane/rotation and start/end standoffs.
    if (boltGroup.Position != null)
    {
      properties["positionPlane"] = boltGroup.Position.Plane.ToString();
      properties["positionDepth"] = boltGroup.Position.Depth.ToString();
      properties["positionRotation"] = boltGroup.Position.Rotation.ToString();
      properties["positionPlaneOffset"] = boltGroup.Position.PlaneOffset;
      properties["positionDepthOffset"] = boltGroup.Position.DepthOffset;
      properties["positionRotationOffset"] = boltGroup.Position.RotationOffset;
    }
    if (boltGroup.StartPointOffset != null)
    {
      properties["startPointOffsetDx"] = boltGroup.StartPointOffset.Dx;
      properties["startPointOffsetDy"] = boltGroup.StartPointOffset.Dy;
      properties["startPointOffsetDz"] = boltGroup.StartPointOffset.Dz;
    }
    if (boltGroup.EndPointOffset != null)
    {
      properties["endPointOffsetDx"] = boltGroup.EndPointOffset.Dx;
      properties["endPointOffsetDy"] = boltGroup.EndPointOffset.Dy;
      properties["endPointOffsetDz"] = boltGroup.EndPointOffset.Dz;
    }

    switch (boltGroup)
    {
      case TSM.BoltArray array:
        properties["patternType"] = "Array";
        properties["xDistances"] = ExtractBoltDistancesX(array);
        properties["yDistances"] = ExtractBoltDistancesY(array);
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
      case TSM.BoltXYList xyList:
        properties["patternType"] = "XY";
        properties["xCoordinates"] = ExtractBoltDistancesX(xyList);
        properties["yCoordinates"] = ExtractBoltDistancesY(xyList);
        properties["startPoint"] = ExtractPoint(xyList.FirstPosition);
        properties["endPoint"] = ExtractPoint(xyList.SecondPosition);
        break;
    }

    // A bolt group always connects a main part ("PartToBeBolted") to a required primary
    // secondary part ("PartToBoltTo" — may equal the main part for single-ply connections
    // such as anchor bolts/shear studs), optionally joined by further parts
    // ("OtherPartsToBolt") for multi-ply connections.
    if (boltGroup.PartToBeBolted != null)
    {
      properties["mainPartId"] = boltGroup.PartToBeBolted.GetSpeckleApplicationId();
    }

    string? partToBoltToId = null;
    if (boltGroup.PartToBoltTo != null)
    {
      partToBoltToId = boltGroup.PartToBoltTo.GetSpeckleApplicationId();
      properties["partToBoltToId"] = partToBoltToId;
    }

    var secondaryPartIds = new List<string>();
    foreach (TSM.Part p in boltGroup.OtherPartsToBolt)
    {
      var id = p.GetSpeckleApplicationId();
      if (id != partToBoltToId)
      {
        secondaryPartIds.Add(id);
      }
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

  // BoltArray/BoltXYList expose their bolt-pitch values via GetBoltDistXCount()/GetBoltDistX(i)
  // (and the Y equivalents) — there is no settable XDistance/YDistance/XCoordinate/YCoordinate
  // collection property (the prior `dynBolt.XDistance` access threw RuntimeBinderException,
  // silently swallowed by a try/catch, so every bolt group was captured with empty distances).
  // Both types share these method names but no common base declaring them, hence `dynamic`.
  private static List<double> ExtractBoltDistancesX(dynamic boltGroup)
  {
    var list = new List<double>();
    int count = boltGroup.GetBoltDistXCount();
    for (int i = 0; i < count; i++)
    {
      list.Add(boltGroup.GetBoltDistX(i));
    }
    return list;
  }

  private static List<double> ExtractBoltDistancesY(dynamic boltGroup)
  {
    var list = new List<double>();
    int count = boltGroup.GetBoltDistYCount();
    for (int i = 0; i < count; i++)
    {
      list.Add(boltGroup.GetBoltDistY(i));
    }
    return list;
  }

  private void AddSingleRebarProperties(TSM.SingleRebar singleRebar, Dictionary<string, object?> properties)
  {
    properties["grade"] = singleRebar.Grade;
    properties["size"] = singleRebar.Size;
    properties["class"] = singleRebar.Class;
    // Bend radius per interior corner of the Polygon — Reinforcement.RadiusValues must contain
    // exactly (Points.Count - 2) entries or RebarGroupParametersCheck/Insert() throws
    // "Required information missing - RadiusValues" for any non-straight shape.
    properties["radius_values"] = ExtractDistances(singleRebar.RadiusValues);

    AddRebarHookProperties("start", singleRebar.StartHook, properties);
    AddRebarHookProperties("end", singleRebar.EndHook, properties);
    AddReinforcementOffsetProperties(singleRebar, properties);

    if (singleRebar.Father != null)
    {
      properties["father_id"] = singleRebar.Father.GetSpeckleApplicationId();
    }
  }

  private static void AddRebarHookProperties(
    string prefix,
    TSM.RebarHookData hook,
    Dictionary<string, object?> properties
  )
  {
    properties[$"{prefix}_hook_type"] = hook.Shape.ToString();
    properties[$"{prefix}_hook_angle"] = hook.Angle;
    properties[$"{prefix}_hook_radius"] = hook.Radius;
    properties[$"{prefix}_hook_length"] = hook.Length;
  }

  // Reinforcement (base of SingleRebar/BaseRebarGroup) positions the bar relative to its defining
  // Polygon via these offsets — without them, receive rebuilds the rebar directly on the captured
  // polygon line, which can land it embedded in/overlapping its father part or sibling rebars
  // instead of at its actual cover-adjusted position.
  private void AddReinforcementOffsetProperties(TSM.Reinforcement reinforcement, Dictionary<string, object?> properties)
  {
    properties["on_plane_offsets"] = ExtractDistances(reinforcement.OnPlaneOffsets);
    properties["from_plane_offset"] = reinforcement.FromPlaneOffset;
    properties["start_point_offset_type"] = reinforcement.StartPointOffsetType.ToString();
    properties["start_point_offset_value"] = reinforcement.StartPointOffsetValue;
    properties["end_point_offset_type"] = reinforcement.EndPointOffsetType.ToString();
    properties["end_point_offset_value"] = reinforcement.EndPointOffsetValue;
  }

  private void AddRebarMeshProperties(TSM.RebarMesh rebarMesh, Dictionary<string, object?> properties)
  {
    properties["name"] = rebarMesh.Name;
    properties["grade"] = rebarMesh.Grade;
    properties["class"] = rebarMesh.Class;
    properties["catalogName"] = rebarMesh.CatalogName;

    // The mesh type determines how its geometry is defined: a RECTANGULAR_MESH is built from
    // StartPoint/EndPoint/Length/Width, while POLYGON_MESH/BENT_MESH are built from a Polygon
    // outline (captured separately as `location` by LocationExtractor). The type itself cannot
    // be changed after creation, so it must be set before Insert().
    properties["meshType"] = rebarMesh.MeshType.ToString();
    properties["startPoint"] = ExtractPoint(rebarMesh.StartPoint);
    properties["endPoint"] = ExtractPoint(rebarMesh.EndPoint);
    properties["length"] = rebarMesh.Length;
    properties["width"] = rebarMesh.Width;

    properties["longitudinalSize"] = rebarMesh.LongitudinalSize;
    properties["crossSize"] = rebarMesh.CrossSize;
    properties["longitudinalSpacingMethod"] = rebarMesh.LongitudinalSpacingMethod.ToString();
    properties["longitudinalDistances"] = ExtractDistances(rebarMesh.LongitudinalDistances);
    properties["crossDistances"] = ExtractDistances(rebarMesh.CrossDistances);

    properties["leftOverhangLongitudinal"] = rebarMesh.LeftOverhangLongitudinal;
    properties["rightOverhangLongitudinal"] = rebarMesh.RightOverhangLongitudinal;
    properties["leftOverhangCross"] = rebarMesh.LeftOverhangCross;
    properties["rightOverhangCross"] = rebarMesh.RightOverhangCross;

    properties["crossBarLocation"] = rebarMesh.CrossBarLocation.ToString();
    properties["cutByFatherPartCuts"] = rebarMesh.CutByFatherPartCuts;

    properties["fromPlaneOffset"] = rebarMesh.FromPlaneOffset;
    properties["startFromPlaneOffset"] = rebarMesh.StartFromPlaneOffset;
    properties["endFromPlaneOffset"] = rebarMesh.EndFromPlaneOffset;
    properties["onPlaneOffsets"] = ExtractDistances(rebarMesh.OnPlaneOffsets);

    properties["startPointOffsetType"] = rebarMesh.StartPointOffsetType.ToString();
    properties["startPointOffsetValue"] = rebarMesh.StartPointOffsetValue;
    properties["endPointOffsetType"] = rebarMesh.EndPointOffsetType.ToString();
    properties["endPointOffsetValue"] = rebarMesh.EndPointOffsetValue;

    if (rebarMesh.Father != null)
      properties["father_id"] = rebarMesh.Father.GetSpeckleApplicationId();
  }

  private void AddRebarGroupProperties(TSM.RebarGroup rebarGroup, Dictionary<string, object?> properties)
  {
    properties["grade"] = rebarGroup.Grade;
    properties["size"] = rebarGroup.Size;
    properties["class"] = rebarGroup.Class;
    // Bend radius per interior corner of the Polygon — Reinforcement.RadiusValues must contain
    // exactly (Points.Count - 2) entries or RebarGroupParametersCheck/Insert() throws
    // "Required information missing - RadiusValues" for any non-straight shape.
    properties["radius_values"] = ExtractDistances(rebarGroup.RadiusValues);

    AddRebarHookProperties("start", rebarGroup.StartHook, properties);
    AddRebarHookProperties("end", rebarGroup.EndHook, properties);
    AddReinforcementOffsetProperties(rebarGroup, properties);

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
      fitting.Plane.Origin.Z,
    };
    properties["plane_x"] = new List<double> { fitting.Plane.AxisX.X, fitting.Plane.AxisX.Y, fitting.Plane.AxisX.Z };
    properties["plane_y"] = new List<double> { fitting.Plane.AxisY.X, fitting.Plane.AxisY.Y, fitting.Plane.AxisY.Z };
  }

  private static void AddBooleanPartProperties(TSM.BooleanPart booleanPart, Dictionary<string, object?> properties)
  {
    properties["boolean_type"] = booleanPart.Type.ToString();
    if (booleanPart.OperativePart is TSM.Part opPart)
    {
      properties["operative_type"] = opPart.GetType().Name;
      properties["operative_profile"] = opPart.Profile.ProfileString;
      properties["operative_material"] = opPart.Material.MaterialString;
      properties["operative_class"] = opPart.Class;

      // Capture Position too — depth/plane/rotation determine where the cutting solid sits
      // relative to its profile/contour. Without it, receive rebuilds the operative at Tekla's
      // default depth position, which can shift a horizontal cutter off the father's material
      // (see CreateBooleanPart in SubComponentToHostConverter for the receive-side symptom).
      properties["operative_position_depth"] = opPart.Position.Depth.ToString();
      properties["operative_position_depth_offset"] = opPart.Position.DepthOffset;
      properties["operative_position_plane"] = opPart.Position.Plane.ToString();
      properties["operative_position_plane_offset"] = opPart.Position.PlaneOffset;
      properties["operative_position_rotation"] = opPart.Position.Rotation.ToString();
      properties["operative_position_rotation_offset"] = opPart.Position.RotationOffset;

      // The operative part is a transient cutting tool with no independent existence in the
      // model — its display is suppressed on send (see ModelObjectToSpeckleConverter) and its
      // own geometry is never captured anywhere else. Without it, receive can only build an
      // empty-shell Part (profile string but no contour/points), and BooleanPart.Insert()
      // silently returns false because the operative has nothing to cut/add with. Capture its
      // real geometry directly so receive can reconstruct a usable operative part.
      TSM.Contour? operativeContour = opPart switch
      {
        TSM.ContourPlate contourPlate => contourPlate.Contour,
        TSM.PolyBeam polyBeam => polyBeam.Contour,
        _ => null,
      };
      if (operativeContour?.ContourPoints != null)
      {
        var pts = new List<double>();
        var chamfers = new List<Dictionary<string, object?>>();
        foreach (TSM.ContourPoint cp in operativeContour.ContourPoints)
        {
          pts.Add(cp.X);
          pts.Add(cp.Y);
          pts.Add(cp.Z);
          // Mirrors GetPolylineFromPoints' "chamfers" sidecar (LocationExtractor) — without this,
          // a cutter's chamfered/rounded corners are silently dropped on receive (CreateBooleanPart
          // always built `new TSM.Chamfer()`), so the resulting cut shows sharp corners where the
          // source had chamfers.
          chamfers.Add(
            new Dictionary<string, object?>
            {
              ["type"] = cp.Chamfer.Type.ToString(),
              ["x"] = cp.Chamfer.X,
              ["y"] = cp.Chamfer.Y,
            }
          );
        }
        properties["operative_contour_points"] = pts;
        properties["operative_contour_chamfers"] = chamfers;
      }
      else if (opPart is TSM.Beam beam)
      {
        properties["operative_start"] = new List<double> { beam.StartPoint.X, beam.StartPoint.Y, beam.StartPoint.Z };
        properties["operative_end"] = new List<double> { beam.EndPoint.X, beam.EndPoint.Y, beam.EndPoint.Z };
      }
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

    // Geometry/placement attributes — without these, every reconstructed weld defaults to the
    // same direction/position/pattern, so multiple welds along the same joint render stacked
    // on top of each other instead of at their original locations.
    properties["direction"] = new List<double> { weld.Direction.X, weld.Direction.Y, weld.Direction.Z };
    properties["position"] = weld.Position.ToString();
    properties["intermittent_type"] = weld.IntermittentType.ToString();
    properties["length_above"] = weld.LengthAbove;
    properties["length_below"] = weld.LengthBelow;
    properties["pitch_above"] = weld.PitchAbove;
    properties["pitch_below"] = weld.PitchBelow;
    properties["placement"] = weld.Placement.ToString();

    if (weld.MainObject != null)
    {
      properties["main_id"] = weld.MainObject.GetSpeckleApplicationId();
    }
    if (weld.SecondaryObject != null)
    {
      properties["secondary_id"] = weld.SecondaryObject.GetSpeckleApplicationId();
    }
  }

  private static void AddBentPlateProperties(TSM.BentPlate bentPlate, Dictionary<string, object?> properties)
  {
    // BentPlate inherits all Part properties (profile, material, position, etc.) via AddPartProperties.
    // The bend shape itself lives in BentPlate.Geometry (a ConnectiveGeometry) — a sequence of
    // GeometrySections that alternate between leg contours (PolygonNode) and bend surfaces
    // (BendSurfaceNode/CylindricalSurface|ConicalSurface). Capture that sequence verbatim so
    // BentPlateGeometrySolver can rebuild the same ConnectiveGeometry on receive.
    // Thickness is read-only — derived from Profile/Geometry, not an independent settable value;
    // the captured `profile` string (via AddPartProperties → TeklaPartPropertyApplicator.Apply)
    // is what actually drives it on receive.

    var sections = new List<Dictionary<string, object?>>();
    var enumerator = bentPlate.Geometry?.GetGeometryEnumerator();
    while (enumerator != null && enumerator.MoveNext())
    {
      var node = enumerator.Current?.GeometryNode;
      switch (node)
      {
        case TSM.PolygonNode polygonNode:
        {
          var pts = new List<double>();
          if (polygonNode.Contour?.ContourPoints != null)
          {
            foreach (TSM.ContourPoint cp in polygonNode.Contour.ContourPoints)
            {
              pts.Add(cp.X);
              pts.Add(cp.Y);
              pts.Add(cp.Z);
            }
          }
          sections.Add(new Dictionary<string, object?> { ["kind"] = "leg", ["contour_points"] = pts });
          break;
        }
        case TSM.BendSurfaceNode bendNode:
        {
          var data = new Dictionary<string, object?> { ["kind"] = "bend" };
          switch (bendNode.Surface)
          {
            case TSM.CylindricalSurface cyl:
              data["bend_shape"] = "Cylindrical";
              data["radius"] = cyl.Radius;
              break;
            case TSM.ConicalSurface con:
              data["bend_shape"] = "Conical";
              data["radius1"] = con.Radiuses.Item1;
              data["radius2"] = con.Radiuses.Item2;
              break;
          }
          sections.Add(data);
          break;
        }
      }
    }
    properties["geometry_sections"] = sections;
  }

  private void AddGridProperties(TSM.Grid grid, Dictionary<string, object?> properties)
  {
    properties["name"] = grid.Name;
    // Raw coordinate strings are the authoritative source for reconstruction
    properties["coordinate_x"] = grid.CoordinateX;
    properties["coordinate_y"] = grid.CoordinateY;
    properties["coordinate_z"] = grid.CoordinateZ;
    properties["label_x"] = grid.LabelX;
    properties["label_y"] = grid.LabelY;
    properties["label_z"] = grid.LabelZ;
    properties["origin"] = new List<double> { grid.Origin.X, grid.Origin.Y, grid.Origin.Z };
    properties["extension_left_x"] = grid.ExtensionLeftX;
    properties["extension_right_x"] = grid.ExtensionRightX;
    properties["extension_left_y"] = grid.ExtensionLeftY;
    properties["extension_right_y"] = grid.ExtensionRightY;
  }

  private void AddSpiralBeamProperties(TSM.SpiralBeam spiralBeam, Dictionary<string, object?> properties)
  {
    properties["total_rise"] = spiralBeam.TotalRise;
    properties["rotation_angle"] = spiralBeam.RotationAngle;
    properties["twist_angle_start"] = spiralBeam.TwistAngleStart;
    properties["twist_angle_end"] = spiralBeam.TwistAngleEnd;
    properties["rotation_axis_base"] = new List<double>
    {
      spiralBeam.RotationAxisBasePoint.X,
      spiralBeam.RotationAxisBasePoint.Y,
      spiralBeam.RotationAxisBasePoint.Z,
    };
    properties["rotation_axis_up"] = new List<double>
    {
      spiralBeam.RotationAxisUpPoint.X,
      spiralBeam.RotationAxisUpPoint.Y,
      spiralBeam.RotationAxisUpPoint.Z,
    };
    properties["rotation_center"] = new List<double>
    {
      spiralBeam.RotationCenterPoint.X,
      spiralBeam.RotationCenterPoint.Y,
      spiralBeam.RotationCenterPoint.Z,
    };
    properties["rotation_axis_dir"] = new List<double>
    {
      spiralBeam.RotationAxisDirection.X,
      spiralBeam.RotationAxisDirection.Y,
      spiralBeam.RotationAxisDirection.Z,
    };
  }

  private void AddLoftedPlateProperties(TSM.LoftedPlate loftedPlate, Dictionary<string, object?> properties)
  {
    properties["face_type"] = loftedPlate.FaceType.ToString();

    var curves = new List<List<double>>();
    foreach (var curveObj in loftedPlate.BaseCurves)
    {
      var pts = new List<double>();
      if (curveObj is TG.LineSegment seg)
      {
        pts.Add(seg.Point1.X);
        pts.Add(seg.Point1.Y);
        pts.Add(seg.Point1.Z);
        pts.Add(seg.Point2.X);
        pts.Add(seg.Point2.Y);
        pts.Add(seg.Point2.Z);
      }
      if (pts.Count > 0)
        curves.Add(pts);
    }
    properties["base_curves"] = curves;
  }

  private void AddCutPlaneProperties(TSM.CutPlane cutPlane, Dictionary<string, object?> properties)
  {
    properties["plane_origin"] = new List<double>
    {
      cutPlane.Plane.Origin.X,
      cutPlane.Plane.Origin.Y,
      cutPlane.Plane.Origin.Z,
    };
    properties["plane_x"] = new List<double> { cutPlane.Plane.AxisX.X, cutPlane.Plane.AxisX.Y, cutPlane.Plane.AxisX.Z };
    properties["plane_y"] = new List<double> { cutPlane.Plane.AxisY.X, cutPlane.Plane.AxisY.Y, cutPlane.Plane.AxisY.Z };
    if (cutPlane.Father != null)
      properties["father_id"] = cutPlane.Father.GetSpeckleApplicationId();
  }

  private void AddRadialGridProperties(TSM.RadialGrid radialGrid, Dictionary<string, object?> properties)
  {
    properties["name"] = radialGrid.Name;
    properties["radial_coordinates"] = radialGrid.RadialCoordinates;
    properties["angular_coordinates"] = radialGrid.AngularCoordinates;
    properties["coordinate_z"] = radialGrid.CoordinateZ;
    properties["radial_labels"] = radialGrid.RadialLabels;
    properties["angular_labels"] = radialGrid.AngularLabels;
    properties["label_z"] = radialGrid.LabelZ;
    properties["arc_start_extension"] = radialGrid.ArcStartExtension;
    properties["arc_end_extension"] = radialGrid.ArcEndExtension;
    properties["angular_lines_start_extension"] = radialGrid.AngularLinesStartExtension;
    properties["angular_lines_end_extension"] = radialGrid.AngularLinesEndExtension;
    properties["extension_below_z"] = radialGrid.ExtensionBelowZ;
    properties["extension_above_z"] = radialGrid.ExtensionAboveZ;
    properties["origin"] = new List<double> { radialGrid.Origin.X, radialGrid.Origin.Y, radialGrid.Origin.Z };
  }

  private void AddEdgeChamferProperties(TSM.EdgeChamfer edgeChamfer, Dictionary<string, object?> properties)
  {
    if (edgeChamfer.Father != null)
      properties["father_id"] = edgeChamfer.Father.GetSpeckleApplicationId();

    properties["chamfer_type"] = edgeChamfer.Chamfer.Type.ToString();
    properties["chamfer_x"] = edgeChamfer.Chamfer.X;
    properties["chamfer_y"] = edgeChamfer.Chamfer.Y;
    properties["first_bevel"] = edgeChamfer.FirstBevelDimension;
    properties["second_bevel"] = edgeChamfer.SecondBevelDimension;
    properties["first_chamfer_end_type"] = edgeChamfer.FirstChamferEndType.ToString();
    properties["second_chamfer_end_type"] = edgeChamfer.SecondChamferEndType.ToString();
    properties["first_end"] = new List<double>
    {
      edgeChamfer.FirstEnd.X,
      edgeChamfer.FirstEnd.Y,
      edgeChamfer.FirstEnd.Z,
    };
    properties["second_end"] = new List<double>
    {
      edgeChamfer.SecondEnd.X,
      edgeChamfer.SecondEnd.Y,
      edgeChamfer.SecondEnd.Z,
    };
  }

  private void AddRebarSetProperties(TSM.RebarSet rebarSet, Dictionary<string, object?> properties)
  {
    if (rebarSet.FatherPart != null)
    {
      properties["father_id"] = rebarSet.FatherPart.GetSpeckleApplicationId();
    }
    else
    {
      // Some RebarSets (e.g. ones built by "follow edges" guidelines spanning a part's faces)
      // leave FatherPart null even though the set is clearly attached to a host part — without
      // a father_id, receive's Pass2b orphan-resolution can't place the set at all (it logs
      // "could not resolve parent via reference id=null" and silently drops it). Fall back to
      // the Father of the set's first generated bar, which Tekla always populates.
      foreach (TSM.ModelObject reinforcement in rebarSet.GetReinforcements())
      {
        if (reinforcement is TSM.Reinforcement reinf && reinf.Father != null)
        {
          properties["father_id"] = reinf.Father.GetSpeckleApplicationId();
          break;
        }
      }
    }
    properties["layer_order_number"] = rebarSet.LayerOrderNumber;

    // BarOrientation defines the line the bars run along/face — without it Tekla derives a
    // default orientation from the guideline geometry, which can place each bar's cross-section
    // facing the wrong way and make adjacent sets overlap.
    if (rebarSet.BarOrientation != null)
    {
      properties["bar_orientation_start"] = new List<double>
      {
        rebarSet.BarOrientation.Point1.X,
        rebarSet.BarOrientation.Point1.Y,
        rebarSet.BarOrientation.Point1.Z,
      };
      properties["bar_orientation_end"] = new List<double>
      {
        rebarSet.BarOrientation.Point2.X,
        rebarSet.BarOrientation.Point2.Y,
        rebarSet.BarOrientation.Point2.Z,
      };
    }

    var rp = rebarSet.RebarProperties;
    if (rp != null)
    {
      properties["rebar_size"] = rp.Size;
      properties["rebar_grade"] = rp.Grade;
      properties["rebar_name"] = rp.Name;
      properties["rebar_class"] = rp.Class;
      properties["bending_radius"] = rp.BendingRadius;
    }

    var legFacesData = new List<Dictionary<string, object?>>();
    foreach (TSM.RebarLegFace lf in rebarSet.LegFaces)
    {
      var lfData = new Dictionary<string, object?>
      {
        ["additional_offset"] = lf.AdditonalOffset,
        ["layer_order_number"] = lf.LayerOrderNumber,
        ["reversed"] = lf.Reversed,
      };
      var pts = new List<double>();
      if (lf.Contour?.ContourPoints != null)
      {
        foreach (TSM.ContourPoint cp in lf.Contour.ContourPoints)
        {
          pts.Add(cp.X);
          pts.Add(cp.Y);
          pts.Add(cp.Z);
        }
      }
      lfData["contour_points"] = pts;
      legFacesData.Add(lfData);
    }
    properties["leg_faces"] = legFacesData;

    var guidelinesData = new List<Dictionary<string, object?>>();
    foreach (TSM.RebarGuideline gl in rebarSet.Guidelines)
    {
      var glData = new Dictionary<string, object?> { ["follow_edges"] = gl.FollowEdges };
      var pts = new List<double>();
      if (gl.Curve?.ContourPoints != null)
      {
        foreach (TSM.ContourPoint cp in gl.Curve.ContourPoints)
        {
          pts.Add(cp.X);
          pts.Add(cp.Y);
          pts.Add(cp.Z);
        }
      }
      glData["curve_points"] = pts;
      if (gl.Spacing != null)
      {
        var spacing = gl.Spacing;
        // RebarSpacing must be reconstructed via RebarSpacing.Create(...) on receive — its
        // constituent data (type, offsets, and the type-specific distance/count/elements) all
        // need to be captured so the right factory overload can be called.
        glData["spacing_type"] = spacing.Type.ToString();
        glData["spacing_bars"] = spacing.NumberOfBars;
        glData["spacing_target"] = spacing.TargetSpace;
        glData["spacing_exact"] = spacing.ExactSpace;
        glData["spacing_start_offset_automatic"] = spacing.StartOffsetIsAutomatic;
        glData["spacing_start_offset"] = spacing.StartOffset;
        glData["spacing_end_offset_automatic"] = spacing.EndOffsetIsAutomatic;
        glData["spacing_end_offset"] = spacing.EndOffset;

        var exactElements = new List<double>();
        if (spacing.ExactElements != null)
        {
          foreach (TSM.RebarSpacing.ExactSpacing.Element element in spacing.ExactElements)
          {
            exactElements.Add(element.Number);
            exactElements.Add(element.Distance);
          }
        }
        glData["spacing_exact_elements"] = exactElements;
      }
      guidelinesData.Add(glData);
    }
    properties["guidelines"] = guidelinesData;
  }
}
