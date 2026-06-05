using System;
using System.Collections.Generic;
using System.Linq;
using Speckle.Converters.Common;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.TeklaShared.ToHost;

public class SubComponentToHostConverter(TeklaReceiveCache receiveCache)
{
  private readonly TeklaReceiveCache _receiveCache = receiveCache;

  public void ConvertAndAttach(TeklaObject target, TSM.ModelObject parent)
  {
    switch (target.Type)
    {
      case "BoltArray":
      case "BoltGroup":
        CreateBoltGroup(target, parent);
        break;
      case "Fitting":
        CreateFitting(target, parent);
        break;
      case "BooleanPart":
        CreateBooleanPart(target, parent);
        break;
      case "Weld":
        CreateWeld(target, parent);
        break;
      case "SingleRebar":
      case "RebarGroup":
        CreateRebar(target, parent);
        break;
      default:
        break;
    }
  }

  private void CreateRebar(TeklaObject target, TSM.ModelObject parent)
  {
    TSM.Part? fatherPart = parent as TSM.Part;
    if (target.Properties.TryGetValue("father_id", out var fId) && fId != null)
    {
      fatherPart = _receiveCache.Get(fId.ToString()) as TSM.Part;
    }

    if (fatherPart == null)
    {
      return;
    }

    TSM.Reinforcement rebar;
    if (target.Type == "SingleRebar")
    {
      rebar = new TSM.SingleRebar();
    }
    else
    {
      rebar = new TSM.RebarGroup();
    }

    rebar.Father = fatherPart;

    if (target.Properties.TryGetValue("grade", out var grade) && grade != null)
    {
      rebar.Grade = grade.ToString();
    }

    if (rebar is TSM.SingleRebar sr)
    {
      if (target.Properties.TryGetValue("size", out var size) && size != null)
      {
        sr.Size = size.ToString();
      }
      if (target.Properties.TryGetValue("class", out var cls) && cls != null)
      {
        sr.Class = System.Convert.ToInt32(cls!);
      }
    }
    else if (rebar is TSM.RebarGroup rg)
    {
      if (target.Properties.TryGetValue("size", out var size) && size != null)
      {
        rg.Size = size.ToString();
      }
      if (target.Properties.TryGetValue("class", out var cls) && cls != null)
      {
        rg.Class = System.Convert.ToInt32(cls!);
      }
    }

    // Apply Geometry (Points)
    if (target.Location is SOG.Polyline polyline)
    {
      var tgPoints = GetPointsFromPolyline(polyline);
      TSM.Polygon polygon = new TSM.Polygon();
      foreach (var pt in tgPoints)
      {
        polygon.Points.Add(pt);
      }

      if (rebar is TSM.SingleRebar srGeo)
      {
        srGeo.Polygon = polygon;
      }
      else if (rebar is TSM.RebarGroup rgGeo)
      {
        rgGeo.Polygons.Add(polygon);
      }
    }

    rebar.Insert();
  }

  private List<TG.Point> GetPointsFromPolyline(SOG.Polyline polyline)
  {
    var pts = new List<TG.Point>();
    for (int i = 0; i < polyline.value.Count; i += 3)
    {
      pts.Add(new TG.Point(polyline.value[i], polyline.value[i + 1], polyline.value[i + 2]));
    }
    return pts;
  }

  private void CreateWeld(TeklaObject target, TSM.ModelObject parent)
  {
    TSM.Weld weld = new TSM.Weld();

    weld.MainObject = parent;
    if (target.Properties.TryGetValue("main_id", out var mId) && mId != null)
    {
      var cachedMain = _receiveCache.Get(mId.ToString());
      if (cachedMain != null)
      {
        weld.MainObject = cachedMain;
      }
    }

    if (target.Properties.TryGetValue("secondary_id", out var sId) && sId != null)
    {
      weld.SecondaryObject = _receiveCache.Get(sId.ToString());
    }

    if (weld.MainObject == null || weld.SecondaryObject == null)
    {
      return;
    }

    if (target.Properties.TryGetValue("size_above", out var sa))
    {
      weld.SizeAbove = System.Convert.ToDouble(sa);
    }
    if (target.Properties.TryGetValue("size_below", out var sb))
    {
      weld.SizeBelow = System.Convert.ToDouble(sb);
    }

    if (target.Properties.TryGetValue("type_above", out var ta) && ta != null)
    {
      weld.TypeAbove = (TSM.BaseWeld.WeldTypeEnum)Enum.Parse(typeof(TSM.BaseWeld.WeldTypeEnum), ta.ToString());
    }
    if (target.Properties.TryGetValue("type_below", out var tb) && tb != null)
    {
      weld.TypeBelow = (TSM.BaseWeld.WeldTypeEnum)Enum.Parse(typeof(TSM.BaseWeld.WeldTypeEnum), tb.ToString());
    }

    weld.Insert();
  }

  private void CreateBoltGroup(TeklaObject target, TSM.ModelObject parent)
  {
    if (parent is not TSM.Part mainPart)
    {
      return;
    }

    TSM.BoltGroup boltGroup;
    var patternType = target.Properties["patternType"]?.ToString();

    switch (patternType)
    {
      case "Circle":
        boltGroup = new TSM.BoltCircle();
        break;
      default:
        boltGroup = new TSM.BoltArray();
        break;
    }

    boltGroup.PartToBeBolted = mainPart;

    if (target.Properties.TryGetValue("secondaryPartIds", out var idsObj) && idsObj is IEnumerable<object> ids)
    {
      foreach (var id in ids)
      {
        if (_receiveCache.Get(id.ToString()) is TSM.Part secondaryPart)
        {
          boltGroup.AddOtherPartToBolt(secondaryPart);
        }
      }
    }

    if (boltGroup.OtherPartsToBolt.Count == 0)
    {
      boltGroup.PartToBoltTo = mainPart;
    }

    if (target.Properties.TryGetValue("boltSize", out var size) && size != null)
    {
      boltGroup.BoltSize = System.Convert.ToDouble(size);
    }
    if (target.Properties.TryGetValue("boltStandard", out var std) && std != null)
    {
      boltGroup.BoltStandard = std.ToString();
    }
    if (target.Properties.TryGetValue("tolerance", out var tol) && tol != null)
    {
      boltGroup.Tolerance = System.Convert.ToDouble(tol!);
    }

    if (boltGroup is TSM.BoltArray array)
    {
      array.FirstPosition = MapPoint(target.Properties["startPoint"]);
      array.SecondPosition = MapPoint(target.Properties["endPoint"]);
      MapDistances(((dynamic)array).XDistance, target.Properties["xDistances"]);
      MapDistances(((dynamic)array).YDistance, target.Properties["yDistances"]);
    }
    else if (boltGroup is TSM.BoltCircle circle)
    {
      circle.FirstPosition = MapPoint(target.Properties["centerPoint"]);
      circle.SecondPosition = MapPoint(target.Properties["directionPoint"]);
      if (target.Properties.TryGetValue("boltCount", out var bc))
      {
        circle.NumberOfBolts = System.Convert.ToInt32(bc);
      }
      if (target.Properties.TryGetValue("diameter", out var dia))
      {
        circle.Diameter = System.Convert.ToDouble(dia);
      }
    }

    boltGroup.Insert();
  }

  private TG.Point MapPoint(object? obj)
  {
    if (obj is List<double> pts)
    {
      return new TG.Point(pts[0], pts[1], pts[2]);
    }
    if (obj is IEnumerable<object> ptsObj)
    {
      var list = ptsObj.Select(p => System.Convert.ToDouble(p)).ToList();
      return new TG.Point(list[0], list[1], list[2]);
    }
    return new TG.Point(0, 0, 0);
  }

  private void MapDistances(dynamic collection, object? distancesObj)
  {
    if (distancesObj is System.Collections.IEnumerable dists)
    {
      foreach (var d in dists)
      {
        collection.Add(System.Convert.ToDouble(d));
      }
    }
  }

  private void CreateFitting(TeklaObject target, TSM.ModelObject parent)
  {
    if (parent is not TSM.Part part)
    {
      return;
    }

    TSM.Fitting fitting = new TSM.Fitting();
    fitting.Father = part;

    fitting.Plane = new TSM.Plane();
    if (target.Properties.TryGetValue("plane_origin", out var originObj))
    {
      fitting.Plane.Origin = MapPoint(originObj);
    }

    fitting.Insert();
  }

  private void CreateBooleanPart(TeklaObject target, TSM.ModelObject parent)
  {
    if (parent is not TSM.Part fatherPart)
    {
      return;
    }

    TSM.BooleanPart booleanPart = new TSM.BooleanPart();
    booleanPart.Father = fatherPart;

    TSM.Part? operativePart = null;
    target.Properties.TryGetValue("operative_type", out var typeObj);
    var typeStr = typeObj?.ToString();

    switch (typeStr)
    {
      case "Beam":
        operativePart = new TSM.Beam();
        break;
      case "PolyBeam":
        operativePart = new TSM.PolyBeam();
        break;
      case "ContourPlate":
        operativePart = new TSM.ContourPlate();
        break;
      default:
        operativePart = new TSM.ContourPlate();
        break;
    }

    if (target.Properties.TryGetValue("operative_profile", out var prof) && prof != null)
    {
      operativePart.Profile.ProfileString = prof.ToString();
    }
    if (target.Properties.TryGetValue("operative_material", out var mat) && mat != null)
    {
      operativePart.Material.MaterialString = mat.ToString();
    }
    if (target.Properties.TryGetValue("operative_class", out var cls) && cls != null)
    {
      operativePart.Class = cls.ToString();
    }

    if (target.Location is SOG.Polyline polyline)
    {
      ApplyContourToPart(operativePart, polyline);
    }
    else if (target.Location is SOG.Line line && operativePart is TSM.Beam beam)
    {
      beam.StartPoint = new TG.Point(line.start.x, line.start.y, line.start.z);
      beam.EndPoint = new TG.Point(line.end.x, line.end.y, line.end.z);
    }

    booleanPart.OperativePart = operativePart;
    if (target.Properties.TryGetValue("type", out var bType) && bType != null)
    {
      booleanPart.Type = (TSM.BooleanPart.BooleanTypeEnum)
        Enum.Parse(typeof(TSM.BooleanPart.BooleanTypeEnum), bType.ToString());
    }

    booleanPart.Insert();
  }

  private void ApplyContourToPart(TSM.Part part, SOG.Polyline polyline)
  {
    var tgPoints = GetPointsFromPolyline(polyline);
    var chamfers = polyline["chamfers"] as System.Collections.IEnumerable;

    var chamferList = chamfers?.Cast<object>().ToList();

    for (int i = 0; i < tgPoints.Count; i++)
    {
      var cp = new TSM.ContourPoint(tgPoints[i], new TSM.Chamfer());
      if (chamferList != null && i < chamferList.Count && chamferList[i] is IDictionary<string, object> chMap)
      {
        cp.Chamfer.X = System.Convert.ToDouble(chMap["x"] ?? 0.0);
        cp.Chamfer.Y = System.Convert.ToDouble(chMap["y"] ?? 0.0);
        if (chMap.TryGetValue("type", out var typeStr))
        {
          cp.Chamfer.Type = (TSM.Chamfer.ChamferTypeEnum)
            Enum.Parse(typeof(TSM.Chamfer.ChamferTypeEnum), typeStr.ToString());
        }
      }

      if (part is TSM.ContourPlate cpPlate)
      {
        cpPlate.AddContourPoint(cp);
      }
      else if (part is TSM.PolyBeam pBeam)
      {
        pBeam.AddContourPoint(cp);
      }
    }
  }
}
