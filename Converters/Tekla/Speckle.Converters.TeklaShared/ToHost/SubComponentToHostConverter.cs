using Speckle.Objects.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.TeklaShared.Extensions;
using Speckle.Converters.TeklaShared.Helpers;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.TeklaShared.ToHost;

public class SubComponentToHostConverter(TeklaReceiveCache receiveCache, ILogger<SubComponentToHostConverter> logger)
{
  private readonly TeklaReceiveCache _receiveCache = receiveCache;
  private readonly ILogger<SubComponentToHostConverter> _logger = logger;

  public void ConvertAndAttach(TeklaObject target, TSM.ModelObject parent)
  {
    _logger.LogInformation(
      "      ConvertAndAttach type={Type} id={Id} -> parent={ParentType} identifier={ParentId}",
      target.type,
      target.id,
      parent.GetType().Name,
      parent.Identifier
    );

    switch (target.type)
    {
      case "BoltArray":
      case "BoltCircle":
      case "BoltXY":
        CreateBoltGroup(target, parent);
        break;
      case "Fitting":
        CreateFitting(target, parent);
        break;
      case "BooleanPart":
      case "BOOLEAN_CUT":
      case "BOOLEAN_ADD":
        CreateBooleanPart(target, parent);
        break;
      case "CutPlane":
        CreateCutPlane(target, parent);
        break;
      case "EdgeChamfer":
        CreateEdgeChamfer(target, parent);
        break;
      case "Weld":
        CreateWeld(target, parent);
        break;
      case "SingleRebar":
      case "RebarGroup":
        CreateRebar(target, parent);
        break;
      case "RebarMesh":
        CreateRebarMesh(target, parent);
        break;
      case "RebarSet":
        CreateRebarSet(target, parent);
        break;
      default:
        _logger.LogWarning("      ConvertAndAttach: unhandled sub-component type={Type} id={Id}", target.type, target.id);
        break;
    }
  }

  private void LogInsertResult(string typeName, TSM.ModelObject obj, bool inserted)
  {
    if (inserted)
    {
      _logger.LogInformation("      {Type} inserted OK identifier={Id}", typeName, obj.Identifier);
    }
    else
    {
      _logger.LogWarning("      {Type} Insert() returned FALSE (not created) identifier={Id}", typeName, obj.Identifier);
    }
  }

  private void CreateRebar(TeklaObject target, TSM.ModelObject parent)
  {
    TSM.Part? fatherPart = parent as TSM.Part;
    if (target.properties.TryGetValue("father_id", out var fId) && fId != null)
    {
      fatherPart = _receiveCache.Get(fId.ToString()) as TSM.Part;
    }

    if (fatherPart == null)
    {
      _logger.LogWarning("      CreateRebar: father part could not be resolved (father_id={FatherId}, parent={ParentType})", fId?.ToString(), parent.GetType().Name);
      return;
    }

    TSM.Reinforcement rebar;
    if (target.type == "SingleRebar")
    {
      rebar = new TSM.SingleRebar();
    }
    else
    {
      rebar = new TSM.RebarGroup();
    }

    rebar.Father = fatherPart;

    if (target.properties.TryGetValue("grade", out var grade) && grade != null)
    {
      rebar.Grade = grade.ToString();
    }

    if (rebar is TSM.SingleRebar sr)
    {
      if (target.properties.TryGetValue("size", out var size) && size != null)
      {
        sr.Size = size.ToString();
      }
      if (target.properties.TryGetValue("class", out var cls) && cls != null)
      {
        sr.Class = System.Convert.ToInt32(cls!);
      }
      ApplyRebarHookProperties("start", sr.StartHook, target.properties);
      ApplyRebarHookProperties("end", sr.EndHook, target.properties);
    }
    else if (rebar is TSM.RebarGroup rg)
    {
      if (target.properties.TryGetValue("size", out var size) && size != null)
        rg.Size = size.ToString();
      if (target.properties.TryGetValue("class", out var cls) && cls != null)
        rg.Class = System.Convert.ToInt32(cls!);
      ApplyRebarHookProperties("start", rg.StartHook, target.properties);
      ApplyRebarHookProperties("end", rg.EndHook, target.properties);
    }

    // Reinforcement offsets — these position the bar relative to the captured Polygon. Without
    // them the rebar lands directly on the polygon line instead of at its cover-adjusted position,
    // which can overlap the father part's surface or sibling rebars.
    if (target.properties.TryGetValue("on_plane_offsets", out var opo) && opo != null)
    {
      rebar.OnPlaneOffsets.Clear();
      MapDistances(rebar.OnPlaneOffsets, opo);
    }
    if (target.properties.TryGetValue("from_plane_offset", out var fpo) && fpo != null)
      rebar.FromPlaneOffset = System.Convert.ToDouble(fpo);
    if (target.properties.TryGetValue("start_point_offset_type", out var spot) && spot != null
        && Enum.TryParse<TSM.Reinforcement.RebarOffsetTypeEnum>(spot.ToString(), out var spotEnum))
      rebar.StartPointOffsetType = spotEnum;
    if (target.properties.TryGetValue("start_point_offset_value", out var spov) && spov != null)
      rebar.StartPointOffsetValue = System.Convert.ToDouble(spov);
    if (target.properties.TryGetValue("end_point_offset_type", out var epot) && epot != null
        && Enum.TryParse<TSM.Reinforcement.RebarOffsetTypeEnum>(epot.ToString(), out var epotEnum))
      rebar.EndPointOffsetType = epotEnum;
    if (target.properties.TryGetValue("end_point_offset_value", out var epov) && epov != null)
      rebar.EndPointOffsetValue = System.Convert.ToDouble(epov);

    // Apply Geometry (Points)
    if (target["location"] is SOG.Polyline polyline)
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

      // Tekla requires exactly (Points.Count - 2) bend-radius entries for any shape with interior
      // corners — RebarGroupParametersCheck/Insert() throws "Required information missing -
      // RadiusValues" otherwise. A straight 2-point SingleRebar tolerates an empty list, but
      // RebarGroup's check additionally rejects an empty RadiusValues even for straight 2-point
      // groups (confirmed via receive log: "Required information missing - RadiusValues" thrown
      // with points=2/expectedRadiusCount=0) — so groups always need at least one entry.
      int expectedRadiusCount = Math.Max(0, polygon.Points.Count - 2);
      int requiredRadiusCount = rebar is TSM.RebarGroup ? Math.Max(1, expectedRadiusCount) : expectedRadiusCount;
      if (requiredRadiusCount > 0)
      {
        rebar.RadiusValues.Clear();
        MapDistances(rebar.RadiusValues, target.properties.TryGetValue("radius_values", out var rv) ? rv : null);

        // Pad/trim to the count Tekla expects so Insert() doesn't reject the shape outright —
        // any captured values are kept, missing ones default to 0 (no fillet on that corner).
        while (rebar.RadiusValues.Count < requiredRadiusCount)
        {
          rebar.RadiusValues.Add(0.0);
        }
        while (rebar.RadiusValues.Count > requiredRadiusCount)
        {
          rebar.RadiusValues.RemoveAt(rebar.RadiusValues.Count - 1);
        }
      }

      _logger.LogDebug(
        "      {Type} pre-insert: points={Points} expectedRadiusCount={ExpectedRadiusCount} requiredRadiusCount={RequiredRadiusCount} radiusValues=[{RadiusValues}]",
        target.type,
        polygon.Points.Count,
        expectedRadiusCount,
        requiredRadiusCount,
        string.Join(", ", rebar.RadiusValues.ToArray())
      );
    }

    bool rebarInserted = rebar.Insert();
    LogInsertResult(target.type, rebar, rebarInserted);
    TeklaPartPropertyApplicator.ApplyUdas(rebar, target.properties);
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

  /// <summary>
  /// Newell's method: a polygon normal whose direction encodes winding order (right-hand rule).
  /// Used purely for diagnostic logging while we chase the boolean-operative position/winding bug —
  /// lets us correlate "which way does the captured contour wind" with "did the resulting cut land
  /// on the correct side of the father part" across multiple test cases.
  /// </summary>
  private static (double X, double Y, double Z) ComputeNewellNormal(IReadOnlyList<TG.Point> points)
  {
    double nx = 0,
      ny = 0,
      nz = 0;
    for (int i = 0; i < points.Count; i++)
    {
      var current = points[i];
      var next = points[(i + 1) % points.Count];
      nx += (current.Y - next.Y) * (current.Z + next.Z);
      ny += (current.Z - next.Z) * (current.X + next.X);
      nz += (current.X - next.X) * (current.Y + next.Y);
    }
    return (nx, ny, nz);
  }

  private void CreateWeld(TeklaObject target, TSM.ModelObject parent)
  {
    TSM.Weld weld = new TSM.Weld();

    weld.MainObject = parent;
    if (target.properties.TryGetValue("main_id", out var mId) && mId != null)
    {
      var cachedMain = _receiveCache.Get(mId.ToString());
      if (cachedMain != null)
      {
        weld.MainObject = cachedMain;
      }
    }

    if (target.properties.TryGetValue("secondary_id", out var sId) && sId != null)
    {
      weld.SecondaryObject = _receiveCache.Get(sId.ToString());
    }

    if (weld.MainObject == null || weld.SecondaryObject == null)
    {
      _logger.LogWarning(
        "      CreateWeld: could not resolve MainObject={Main} or SecondaryObject={Secondary} (main_id={MainId}, secondary_id={SecondaryId})",
        weld.MainObject != null,
        weld.SecondaryObject != null,
        mId?.ToString(),
        sId?.ToString()
      );
      return;
    }

    if (target.properties.TryGetValue("size_above", out var sa))
    {
      weld.SizeAbove = System.Convert.ToDouble(sa);
    }
    if (target.properties.TryGetValue("size_below", out var sb))
    {
      weld.SizeBelow = System.Convert.ToDouble(sb);
    }

    if (target.properties.TryGetValue("type_above", out var ta) && ta != null)
      weld.TypeAbove = (TSM.BaseWeld.WeldTypeEnum)Enum.Parse(typeof(TSM.BaseWeld.WeldTypeEnum), ta.ToString());
    if (target.properties.TryGetValue("type_below", out var tb) && tb != null)
      weld.TypeBelow = (TSM.BaseWeld.WeldTypeEnum)Enum.Parse(typeof(TSM.BaseWeld.WeldTypeEnum), tb.ToString());
    if (target.properties.TryGetValue("shop_site", out var ss) && ss != null
        && bool.TryParse(ss.ToString(), out var shopSite))
      weld.ShopWeld = shopSite;
    if (target.properties.TryGetValue("around", out var aw) && aw != null
        && bool.TryParse(aw.ToString(), out var aroundWeld))
      weld.AroundWeld = aroundWeld;

    // Geometry/placement attributes — these determine WHERE along the joint each weld bead is
    // drawn. Without them every weld defaults to the same direction/position/continuous pattern,
    // so multiple welds along the same joint render stacked on top of each other.
    if (target.properties.TryGetValue("direction", out var dir) && dir != null)
    {
      weld.Direction = MapVector(dir);
    }
    if (target.properties.TryGetValue("position", out var pos) && pos != null)
      weld.Position = (TSM.Weld.WeldPositionEnum)Enum.Parse(typeof(TSM.Weld.WeldPositionEnum), pos.ToString());
    if (target.properties.TryGetValue("intermittent_type", out var it) && it != null)
      weld.IntermittentType = (TSM.BaseWeld.WeldIntermittentTypeEnum)
        Enum.Parse(typeof(TSM.BaseWeld.WeldIntermittentTypeEnum), it.ToString());
    if (target.properties.TryGetValue("placement", out var pl) && pl != null)
      weld.Placement = (TSM.BaseWeld.WeldPlacementTypeEnum)Enum.Parse(typeof(TSM.BaseWeld.WeldPlacementTypeEnum), pl.ToString());
    if (target.properties.TryGetValue("length_above", out var la) && la != null)
      weld.LengthAbove = System.Convert.ToDouble(la);
    if (target.properties.TryGetValue("length_below", out var lb) && lb != null)
      weld.LengthBelow = System.Convert.ToDouble(lb);
    if (target.properties.TryGetValue("pitch_above", out var pa) && pa != null)
      weld.PitchAbove = System.Convert.ToDouble(pa);
    if (target.properties.TryGetValue("pitch_below", out var pb) && pb != null)
      weld.PitchBelow = System.Convert.ToDouble(pb);
    _logger.LogDebug(
      "      CreateWeld pre-insert: id={Id} main_id={MainId} secondary_id={SecondaryId} "
        + "MainObject=({MainType} identifier={MainIdentifier}) SecondaryObject=({SecondaryType} identifier={SecondaryIdentifier}) "
        + "sameObject={SameObject} sizeAbove={SizeAbove} sizeBelow={SizeBelow} "
        + "direction=({DirX},{DirY},{DirZ}) position={Position} intermittentType={IntermittentType} "
        + "lengthAbove={LengthAbove} lengthBelow={LengthBelow} pitchAbove={PitchAbove} pitchBelow={PitchBelow} "
        + "placement={Placement}",
      target.id,
      mId?.ToString(),
      sId?.ToString(),
      weld.MainObject.GetType().Name,
      weld.MainObject.Identifier,
      weld.SecondaryObject.GetType().Name,
      weld.SecondaryObject.Identifier,
      weld.MainObject.Identifier.GUID == weld.SecondaryObject.Identifier.GUID,
      weld.SizeAbove,
      weld.SizeBelow,
      weld.Direction.X,
      weld.Direction.Y,
      weld.Direction.Z,
      weld.Position,
      weld.IntermittentType,
      weld.LengthAbove,
      weld.LengthBelow,
      weld.PitchAbove,
      weld.PitchBelow,
      weld.Placement
    );

    bool weldInserted = weld.Insert();
    LogInsertResult(target.type, weld, weldInserted);
    _logger.LogDebug("      CreateWeld post-insert: identifier={Identifier}", weld.Identifier);
    TeklaPartPropertyApplicator.ApplyUdas(weld, target.properties);
  }

  private void CreateBoltGroup(TeklaObject target, TSM.ModelObject parent)
  {
    var props = target.properties;

    // The main part ("PartToBeBolted") is normally the Speckle-tree parent this bolt group
    // was attached to, but resolve it from the cached id when available — the parent in a
    // partial/replace receive is not guaranteed to be the same part that owns the bolt group.
    TSM.Part? mainPart = parent as TSM.Part;
    if (
      props.TryGetValue("mainPartId", out var mpId)
      && mpId != null
      && _receiveCache.Get(mpId.ToString()) is TSM.Part cachedMainPart
    )
    {
      mainPart = cachedMainPart;
    }

    if (mainPart == null)
    {
      _logger.LogWarning("      CreateBoltGroup: main part could not be resolved (mainPartId={MainPartId}, parent={ParentType})", mpId?.ToString(), parent.GetType().Name);
      return;
    }

    TSM.BoltGroup boltGroup;
    props.TryGetValue("patternType", out var patternTypeObj);
    var patternType = patternTypeObj?.ToString();

    switch (patternType)
    {
      case "Circle":
        boltGroup = new TSM.BoltCircle();
        break;
      case "XY":
        // The "XY list" shape is TSM.BoltXYList, NOT a "BoltXY" type — there is no such type
        // in the Open API. Without this case it fell through to the BoltArray default, so the
        // group reconstructed with the wrong shape entirely.
        boltGroup = new TSM.BoltXYList();
        break;
      default:
        boltGroup = new TSM.BoltArray();
        break;
    }

    boltGroup.PartToBeBolted = mainPart;

    // Resolve the secondary part(s) the bolt group connects to. "PartToBoltTo" is the
    // required primary secondary part (Tekla allows it to equal the main part for
    // single-ply connections such as anchor bolts/shear studs); "OtherPartsToBolt" carries
    // any further parts joined in a multi-ply connection.
    TSM.Part? partToBoltTo = null;
    if (
      props.TryGetValue("partToBoltToId", out var pbtId)
      && pbtId != null
      && _receiveCache.Get(pbtId.ToString()) is TSM.Part cachedPartToBoltTo
    )
    {
      partToBoltTo = cachedPartToBoltTo;
    }

    var otherParts = new List<TSM.Part>();
    if (props.TryGetValue("secondaryPartIds", out var idsObj) && idsObj is IEnumerable<object> ids)
    {
      foreach (var id in ids)
      {
        if (_receiveCache.Get(id.ToString()) is TSM.Part secondaryPart)
        {
          otherParts.Add(secondaryPart);
        }
      }
    }

    boltGroup.PartToBoltTo = partToBoltTo ?? otherParts.FirstOrDefault() ?? mainPart;

    foreach (var otherPart in otherParts)
    {
      if (otherPart.GetSpeckleApplicationId() != boltGroup.PartToBoltTo.GetSpeckleApplicationId())
      {
        boltGroup.AddOtherPartToBolt(otherPart);
      }
    }

    if (props.TryGetValue("boltSize", out var size) && size != null)
      boltGroup.BoltSize = System.Convert.ToDouble(size);
    if (props.TryGetValue("boltStandard", out var std) && std != null)
      boltGroup.BoltStandard = std.ToString();
    if (props.TryGetValue("tolerance", out var tol) && tol != null)
      boltGroup.Tolerance = System.Convert.ToDouble(tol!);
    if (props.TryGetValue("boltType", out var bt) && bt != null
        && Enum.TryParse<TSM.BoltGroup.BoltTypeEnum>(bt.ToString(), out var btEnum))
      boltGroup.BoltType = btEnum;
    if (props.TryGetValue("cutLength", out var cl))
      boltGroup.CutLength = System.Convert.ToDouble(cl);
    if (props.TryGetValue("extraLength", out var el))
      boltGroup.ExtraLength = System.Convert.ToDouble(el);
    if (props.TryGetValue("threadInMaterial", out var tim) && tim != null
        && Enum.TryParse<TSM.BoltGroup.BoltThreadInMaterialEnum>(tim.ToString(), out var timEnum))
      boltGroup.ThreadInMaterial = timEnum;
    if (props.TryGetValue("holeType", out var ht) && ht != null
        && Enum.TryParse<TSM.BoltGroup.BoltHoleTypeEnum>(ht.ToString(), out var htEnum))
      boltGroup.HoleType = htEnum;
    if (props.TryGetValue("slottedHoleX", out var shx))
      boltGroup.SlottedHoleX = System.Convert.ToDouble(shx);
    if (props.TryGetValue("slottedHoleY", out var shy))
      boltGroup.SlottedHoleY = System.Convert.ToDouble(shy);
    if (props.TryGetValue("rotateSlots", out var rs) && rs != null)
    {
      // Use reflection — RotateSlots enum type name varies by Tekla API version
      var pi = typeof(TSM.BoltGroup).GetProperty("RotateSlots");
      if (pi?.PropertyType.IsEnum == true)
      {
        try { pi.SetValue(boltGroup, Enum.Parse(pi.PropertyType, rs.ToString())); }
#pragma warning disable CA1031
        catch { }
#pragma warning restore CA1031
      }
    }

    // Position/StartPointOffset/EndPointOffset are base TSM.BoltGroup properties shared by every
    // shape — without applying them the bolt group lands at Tekla's defaults (MIDDLE/MIDDLE/FRONT,
    // zero standoffs) instead of the originally modeled depth/plane/rotation and start/end offsets.
    if (
      props.TryGetValue("positionPlane", out var posPlane) && posPlane != null
      && Enum.TryParse<TSM.Position.PlaneEnum>(posPlane.ToString(), out var planeEnum)
      && props.TryGetValue("positionDepth", out var posDepth) && posDepth != null
      && Enum.TryParse<TSM.Position.DepthEnum>(posDepth.ToString(), out var depthEnum)
      && props.TryGetValue("positionRotation", out var posRotation) && posRotation != null
      && Enum.TryParse<TSM.Position.RotationEnum>(posRotation.ToString(), out var rotationEnum)
    )
    {
      var position = new TSM.Position
      {
        Plane = planeEnum,
        Depth = depthEnum,
        Rotation = rotationEnum,
        PlaneOffset = props.TryGetValue("positionPlaneOffset", out var ppo) && ppo != null ? System.Convert.ToDouble(ppo) : 0.0,
        DepthOffset = props.TryGetValue("positionDepthOffset", out var pdo) && pdo != null ? System.Convert.ToDouble(pdo) : 0.0,
        RotationOffset = props.TryGetValue("positionRotationOffset", out var pro) && pro != null ? System.Convert.ToDouble(pro) : 0.0,
      };
      boltGroup.Position = position;
    }

    if (
      props.TryGetValue("startPointOffsetDx", out var spoDx) && spoDx != null
      && props.TryGetValue("startPointOffsetDy", out var spoDy) && spoDy != null
      && props.TryGetValue("startPointOffsetDz", out var spoDz) && spoDz != null
    )
    {
      boltGroup.StartPointOffset = new TSM.Offset
      {
        Dx = System.Convert.ToDouble(spoDx),
        Dy = System.Convert.ToDouble(spoDy),
        Dz = System.Convert.ToDouble(spoDz),
      };
    }

    if (
      props.TryGetValue("endPointOffsetDx", out var epoDx) && epoDx != null
      && props.TryGetValue("endPointOffsetDy", out var epoDy) && epoDy != null
      && props.TryGetValue("endPointOffsetDz", out var epoDz) && epoDz != null
    )
    {
      boltGroup.EndPointOffset = new TSM.Offset
      {
        Dx = System.Convert.ToDouble(epoDx),
        Dy = System.Convert.ToDouble(epoDy),
        Dz = System.Convert.ToDouble(epoDz),
      };
    }

    if (boltGroup is TSM.BoltArray array)
    {
      array.FirstPosition = MapPoint(props.TryGetValue("startPoint", out var sp) ? sp : null);
      array.SecondPosition = MapPoint(props.TryGetValue("endPoint", out var ep) ? ep : null);
      ApplyBoltDistances(
        array,
        props.TryGetValue("xDistances", out var xd) ? xd : null,
        props.TryGetValue("yDistances", out var yd) ? yd : null
      );
    }
    else if (boltGroup is TSM.BoltCircle circle)
    {
      circle.FirstPosition = MapPoint(props.TryGetValue("centerPoint", out var cp) ? cp : null);
      circle.SecondPosition = MapPoint(props.TryGetValue("directionPoint", out var dp) ? dp : null);
      if (props.TryGetValue("boltCount", out var bc))
        circle.NumberOfBolts = System.Convert.ToInt32(bc);
      if (props.TryGetValue("diameter", out var dia))
        circle.Diameter = System.Convert.ToDouble(dia);
    }
    else if (boltGroup is TSM.BoltXYList xyList)
    {
      // BoltXYList — positions defined by X/Y distance lists relative to start/end reference,
      // applied via AddBoltDistX/Y just like BoltArray (it shares no common base exposing these).
      xyList.FirstPosition = MapPoint(props.TryGetValue("startPoint", out var xysp) ? xysp : null);
      xyList.SecondPosition = MapPoint(props.TryGetValue("endPoint", out var xyep) ? xyep : null);
      ApplyBoltDistances(
        xyList,
        props.TryGetValue("xCoordinates", out var xc) ? xc : null,
        props.TryGetValue("yCoordinates", out var yc) ? yc : null
      );
    }

    bool boltGroupInserted = boltGroup.Insert();
    LogInsertResult(target.type, boltGroup, boltGroupInserted);
    TeklaPartPropertyApplicator.ApplyUdas(boltGroup, target.properties);
  }

  private TG.Point MapPoint(object? obj)
  {
    if (obj is List<double> pts)
      return new TG.Point(pts[0], pts[1], pts[2]);
    if (obj is IEnumerable<object> ptsObj)
    {
      var list = ptsObj.Select(p => System.Convert.ToDouble(p)).ToList();
      return new TG.Point(list[0], list[1], list[2]);
    }
    return new TG.Point(0, 0, 0);
  }

  private void CreateRebarMesh(TeklaObject target, TSM.ModelObject parent)
  {
    var props = target.properties;

    TSM.Part? fatherPart = parent as TSM.Part;
    if (props.TryGetValue("father_id", out var fId) && fId != null)
      fatherPart = _receiveCache.Get(fId?.ToString()) as TSM.Part;
    if (fatherPart == null)
    {
      _logger.LogWarning("      CreateRebarMesh: father part could not be resolved (father_id={FatherId}, parent={ParentType})", fId?.ToString(), parent.GetType().Name);
      return;
    }

    var mesh = new TSM.RebarMesh { Father = fatherPart };

    // The mesh type cannot be changed after creation, so it must be set before Insert() —
    // it also determines whether the geometry comes from StartPoint/EndPoint/Length/Width
    // (RECTANGULAR_MESH) or from a Polygon outline (POLYGON_MESH/BENT_MESH).
    var meshType = TSM.RebarMesh.RebarMeshTypeEnum.POLYGON_MESH;
    if (
      props.TryGetValue("meshType", out var mt)
      && mt != null
      && Enum.TryParse<TSM.RebarMesh.RebarMeshTypeEnum>(mt.ToString(), out var meshTypeEnum)
    )
    {
      meshType = meshTypeEnum;
    }
    mesh.MeshType = meshType;

    if (props.TryGetValue("name", out var name) && name != null)
      mesh.Name = name.ToString();
    if (props.TryGetValue("grade", out var grade) && grade != null)
      mesh.Grade = grade.ToString();
    if (props.TryGetValue("class", out var cls) && cls != null)
      mesh.Class = System.Convert.ToInt32(cls!);
    if (props.TryGetValue("catalogName", out var catalogName) && catalogName != null)
      mesh.CatalogName = catalogName.ToString();

    if (props.TryGetValue("longitudinalSize", out var longSize) && longSize != null)
      mesh.LongitudinalSize = longSize.ToString();
    if (props.TryGetValue("crossSize", out var crossSize) && crossSize != null)
      mesh.CrossSize = crossSize.ToString();
    if (
      props.TryGetValue("longitudinalSpacingMethod", out var spacingMethod)
      && spacingMethod != null
      && Enum.TryParse<TSM.RebarMesh.RebarMeshSpacingMethodEnum>(spacingMethod.ToString(), out var spacingMethodEnum)
    )
    {
      mesh.LongitudinalSpacingMethod = spacingMethodEnum;
    }
    MapDistances(mesh.LongitudinalDistances, props.TryGetValue("longitudinalDistances", out var ld) ? ld : null);
    MapDistances(mesh.CrossDistances, props.TryGetValue("crossDistances", out var cd) ? cd : null);

    if (props.TryGetValue("leftOverhangLongitudinal", out var lol) && lol != null)
      mesh.LeftOverhangLongitudinal = System.Convert.ToDouble(lol);
    if (props.TryGetValue("rightOverhangLongitudinal", out var rol) && rol != null)
      mesh.RightOverhangLongitudinal = System.Convert.ToDouble(rol);
    if (props.TryGetValue("leftOverhangCross", out var loc) && loc != null)
      mesh.LeftOverhangCross = System.Convert.ToDouble(loc);
    if (props.TryGetValue("rightOverhangCross", out var roc) && roc != null)
      mesh.RightOverhangCross = System.Convert.ToDouble(roc);

    if (
      props.TryGetValue("crossBarLocation", out var cbl)
      && cbl != null
      && Enum.TryParse<TSM.RebarMesh.RebarMeshCrossBarLocationEnum>(cbl.ToString(), out var cblEnum)
    )
    {
      mesh.CrossBarLocation = cblEnum;
    }
    if (props.TryGetValue("cutByFatherPartCuts", out var cutByFather) && cutByFather != null)
      mesh.CutByFatherPartCuts = System.Convert.ToBoolean(cutByFather);

    if (props.TryGetValue("fromPlaneOffset", out var fpo) && fpo != null)
      mesh.FromPlaneOffset = System.Convert.ToDouble(fpo);
    if (props.TryGetValue("startFromPlaneOffset", out var sfpo) && sfpo != null)
      mesh.StartFromPlaneOffset = System.Convert.ToDouble(sfpo);
    if (props.TryGetValue("endFromPlaneOffset", out var efpo) && efpo != null)
      mesh.EndFromPlaneOffset = System.Convert.ToDouble(efpo);
    MapDistances(mesh.OnPlaneOffsets, props.TryGetValue("onPlaneOffsets", out var opo) ? opo : null);

    if (
      props.TryGetValue("startPointOffsetType", out var spot)
      && spot != null
      && Enum.TryParse<TSM.Reinforcement.RebarOffsetTypeEnum>(spot.ToString(), out var spotEnum)
    )
    {
      mesh.StartPointOffsetType = spotEnum;
    }
    if (props.TryGetValue("startPointOffsetValue", out var spov) && spov != null)
      mesh.StartPointOffsetValue = System.Convert.ToDouble(spov);
    if (
      props.TryGetValue("endPointOffsetType", out var epot)
      && epot != null
      && Enum.TryParse<TSM.Reinforcement.RebarOffsetTypeEnum>(epot.ToString(), out var epotEnum)
    )
    {
      mesh.EndPointOffsetType = epotEnum;
    }
    if (props.TryGetValue("endPointOffsetValue", out var epov) && epov != null)
      mesh.EndPointOffsetValue = System.Convert.ToDouble(epov);

    if (meshType == TSM.RebarMesh.RebarMeshTypeEnum.RECTANGULAR_MESH)
    {
      mesh.StartPoint = MapPoint(props.TryGetValue("startPoint", out var sp) ? sp : null);
      mesh.EndPoint = MapPoint(props.TryGetValue("endPoint", out var ep) ? ep : null);
      if (props.TryGetValue("length", out var length) && length != null)
        mesh.Length = System.Convert.ToDouble(length);
      if (props.TryGetValue("width", out var width) && width != null)
        mesh.Width = System.Convert.ToDouble(width);
    }
    else if (target["location"] is SOG.Polyline polyline)
    {
      var polygon = new TSM.Polygon();
      for (int i = 0; i * 3 + 2 < polyline.value.Count; i++)
      {
        int idx = i * 3;
        polygon.Points.Add(new TG.Point(polyline.value[idx], polyline.value[idx + 1], polyline.value[idx + 2]));
      }
      mesh.Polygon = polygon;
    }

    _logger.LogDebug(
      "      CreateRebarMesh pre-insert: meshType={MeshType} hasPolygon={HasPolygon} startPoint={StartPoint} endPoint={EndPoint} length={Length} width={Width}",
      mesh.MeshType,
      mesh.Polygon?.Points?.Count > 0,
      mesh.StartPoint,
      mesh.EndPoint,
      mesh.Length,
      mesh.Width
    );
    bool meshInserted = mesh.Insert();
    LogInsertResult(target.type, mesh, meshInserted);
    TeklaPartPropertyApplicator.ApplyUdas(mesh, target.properties);
  }

  private TG.Vector MapVector(object? obj)
  {
    if (obj is List<double> pts)
      return new TG.Vector(pts[0], pts[1], pts[2]);
    if (obj is IEnumerable<object> ptsObj)
    {
      var list = ptsObj.Select(p => System.Convert.ToDouble(p)).ToList();
      return new TG.Vector(list[0], list[1], list[2]);
    }
    return new TG.Vector(1, 0, 0);
  }

  private static void ApplyRebarHookProperties(string prefix, TSM.RebarHookData hook, Dictionary<string, object?> properties)
  {
    if (properties.TryGetValue($"{prefix}_hook_type", out var shape) && shape != null
        && Enum.TryParse<TSM.RebarHookData.RebarHookShapeEnum>(shape.ToString(), out var shapeEnum))
      hook.Shape = shapeEnum;
    if (properties.TryGetValue($"{prefix}_hook_angle", out var angle) && angle != null)
      hook.Angle = System.Convert.ToDouble(angle);
    if (properties.TryGetValue($"{prefix}_hook_radius", out var radius) && radius != null)
      hook.Radius = System.Convert.ToDouble(radius);
    if (properties.TryGetValue($"{prefix}_hook_length", out var length) && length != null)
      hook.Length = System.Convert.ToDouble(length);
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

  // BoltArray/BoltXYList expose their bolt-pitch values via AddBoltDistX/Y(double) methods —
  // unlike most other Tekla collections there is no settable XDistance/YDistance/XCoordinate/
  // YCoordinate property to populate (the prior code's `((dynamic)x).XDistance` access threw
  // RuntimeBinderException: "BoltArray enthält keine Definition für XDistance" on every receive,
  // silently dropping every bolt group). Both types share these method names but no common base
  // exposing them, so `dynamic` dispatch is the simplest way to target both.
  private static void ApplyBoltDistances(dynamic boltGroup, object? xValues, object? yValues)
  {
    if (xValues is System.Collections.IEnumerable xs)
    {
      foreach (var x in xs)
      {
        boltGroup.AddBoltDistX(System.Convert.ToDouble(x));
      }
    }
    if (yValues is System.Collections.IEnumerable ys)
    {
      foreach (var y in ys)
      {
        boltGroup.AddBoltDistY(System.Convert.ToDouble(y));
      }
    }
  }

  private void CreateFitting(TeklaObject target, TSM.ModelObject parent)
  {
    if (parent is not TSM.Part part)
    {
      _logger.LogWarning("      CreateFitting: parent is not a Part (actual={ParentType})", parent.GetType().Name);
      return;
    }

    TSM.Fitting fitting = new TSM.Fitting();
    fitting.Father = part;

    fitting.Plane = new TSM.Plane();
    if (target.properties.TryGetValue("plane_origin", out var originObj))
      fitting.Plane.Origin = MapPoint(originObj);
    if (target.properties.TryGetValue("plane_x", out var pxObj))
      fitting.Plane.AxisX = MapVector(pxObj);
    if (target.properties.TryGetValue("plane_y", out var pyObj))
      fitting.Plane.AxisY = MapVector(pyObj);

    bool fittingInserted = fitting.Insert();
    LogInsertResult(target.type, fitting, fittingInserted);
    TeklaPartPropertyApplicator.ApplyUdas(fitting, target.properties);
  }

  private void CreateBooleanPart(TeklaObject target, TSM.ModelObject parent)
  {
    if (parent is not TSM.Part fatherPart)
    {
      _logger.LogWarning("      CreateBooleanPart: parent is not a Part (actual={ParentType})", parent.GetType().Name);
      return;
    }

    // Every receive of a commit that carries this cut previously ran through here unconditionally,
    // inserting a brand-new native BooleanPart with no awareness of ones inserted by earlier receives
    // of the SAME source cut - since the host Part's identity IS correctly preserved/reused across
    // resends (see TeklaExistingBeamIndex), re-receiving the same model N times stacked N duplicate
    // cuts onto that one host. Mirror the beam/plate mechanism instead: tag inserted cuts with the
    // origin applicationId (TeklaOriginIdentifier) and replace the previously-tagged cut in place.
    string? originApplicationId = target.applicationId ?? target.id;
    if (originApplicationId is not null)
    {
      TSM.BooleanPart? existingCut = FindExistingBooleanCut(fatherPart, originApplicationId);
      if (existingCut is not null)
      {
        bool existingDeleted = existingCut.Delete();
        _logger.LogDebug(
          "      CreateBooleanPart: replacing previously-received cut identifier={Id} deleted={Deleted}",
          existingCut.Identifier,
          existingDeleted
        );
      }
    }

    TSM.BooleanPart booleanPart = new TSM.BooleanPart();
    booleanPart.Father = fatherPart;

    TSM.Part? operativePart = null;
    target.properties.TryGetValue("operative_type", out var typeObj);
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

    if (target.properties.TryGetValue("operative_profile", out var prof) && prof != null)
    {
      operativePart.Profile.ProfileString = prof.ToString();
    }
    if (target.properties.TryGetValue("operative_material", out var mat) && mat != null)
    {
      operativePart.Material.MaterialString = mat.ToString();
    }
    // Tekla.Structures.Model.BooleanPart.set_OperativePart throws "Boolean operative part's class
    // is not BooleanOperativeClass" unless Class == BooleanPart.BooleanOperativeClassName ("BlOpCl")
    // — a sentinel the API enforces on every operative, regardless of whatever class the captured
    // `operative_class` happened to be on the source part. Force it; do not use the captured value.
    operativePart.Class = TSM.BooleanPart.BooleanOperativeClassName;

    // BooleanPart's own `location` is never captured (LocationExtractor has no case for it —
    // it's not a positional Part). The operative's real geometry was captured directly as
    // `operative_contour_points` / `operative_start`+`operative_end` (see AddBooleanPartProperties)
    // because the operative part itself is never converted independently (display suppressed).
    if (
      target.properties.TryGetValue("operative_contour_points", out var ocpObj)
      && ocpObj is System.Collections.IEnumerable ocpEnum
    )
    {
      var coords = ocpEnum.Cast<object>().Select(System.Convert.ToDouble).ToList();
      var chamferList = target.properties.TryGetValue("operative_contour_chamfers", out var occObj)
        && occObj is System.Collections.IEnumerable occEnum
          ? occEnum.Cast<object>().ToList()
          : null;
      var contour = new TSM.Contour();
      // Re-add the captured points in their original order/winding verbatim — this matches the
      // source exactly (same shape, same normal, same reference point ContourPoints[0]). The
      // earlier "flip" turned out NOT to be a winding problem (both winding directions produced
      // a flipped result — see CreateBooleanPart history); the actual cause was Position being
      // applied before the contour existed (see below), so Tekla measured the depth/plane axes
      // off the operative's default pre-contour orientation instead of the final contour plane.
      for (int i = 0; i * 3 + 2 < coords.Count; i++)
      {
        var chamfer = new TSM.Chamfer();
        // Apply the source's per-corner chamfer (captured alongside the points above) instead of
        // always inserting a default/no-chamfer corner — otherwise a cutter with chamfered corners
        // produces a cut whose corners don't match the source (sharp where it should be chamfered,
        // or vice versa).
        if (chamferList != null && i < chamferList.Count && chamferList[i] is IDictionary<string, object> chMap)
        {
          if (chMap.TryGetValue("x", out var chX) && chX != null)
            chamfer.X = System.Convert.ToDouble(chX);
          if (chMap.TryGetValue("y", out var chY) && chY != null)
            chamfer.Y = System.Convert.ToDouble(chY);
          if (
            chMap.TryGetValue("type", out var chType)
            && chType != null
            && Enum.TryParse<TSM.Chamfer.ChamferTypeEnum>(chType.ToString(), out var chTypeEnum)
          )
            chamfer.Type = chTypeEnum;
        }
        contour.AddContourPoint(
          new TSM.ContourPoint(new TG.Point(coords[i * 3], coords[i * 3 + 1], coords[i * 3 + 2]), chamfer)
        );
      }
      // Diagnostic only (chasing the "some cuts float, don't actually cut" bug): log the captured
      // contour's winding (as a Newell normal — its sign/direction encodes winding order) next to
      // the captured depth offset, so we can correlate winding direction with whether the rebuilt
      // cutter ends up on the correct side of the father part across multiple test cases.
      var normal = ComputeNewellNormal(
        Enumerable.Range(0, coords.Count / 3).Select(i => new TG.Point(coords[i * 3], coords[i * 3 + 1], coords[i * 3 + 2])).ToList()
      );
      _logger.LogDebug(
        "      CreateBooleanPart captured contour: pointCount={Count} newellNormal=({Nx:F1},{Ny:F1},{Nz:F1}) capturedDepth={Depth} capturedDepthOffset={DepthOffset} capturedPlane={Plane} capturedPlaneOffset={PlaneOffset}",
        coords.Count / 3,
        normal.X,
        normal.Y,
        normal.Z,
        target.properties.TryGetValue("operative_position_depth", out var logDepth) ? logDepth : "n/a",
        target.properties.TryGetValue("operative_position_depth_offset", out var logDOff) ? logDOff : "n/a",
        target.properties.TryGetValue("operative_position_plane", out var logPlane) ? logPlane : "n/a",
        target.properties.TryGetValue("operative_position_plane_offset", out var logPOff) ? logPOff : "n/a"
      );
      switch (operativePart)
      {
        case TSM.ContourPlate operativeContourPlate:
          operativeContourPlate.Contour = contour;
          break;
        case TSM.PolyBeam operativePolyBeam:
          operativePolyBeam.Contour = contour;
          break;
      }
    }
    else if (
      operativePart is TSM.Beam operativeBeam
      && target.properties.TryGetValue("operative_start", out var startObj)
      && startObj is System.Collections.IEnumerable startEnum
      && target.properties.TryGetValue("operative_end", out var endObj)
      && endObj is System.Collections.IEnumerable endEnum
    )
    {
      var s = startEnum.Cast<object>().Select(System.Convert.ToDouble).ToList();
      var e = endEnum.Cast<object>().Select(System.Convert.ToDouble).ToList();
      if (s.Count >= 3 && e.Count >= 3)
      {
        operativeBeam.StartPoint = new TG.Point(s[0], s[1], s[2]);
        operativeBeam.EndPoint = new TG.Point(e[0], e[1], e[2]);
      }
    }

    // The operative's Position (depth/plane/rotation) was never captured, so a freshly-built
    // operative falls back to Tekla's default (e.g. depth=FRONT instead of the source's MIDDLE).
    // Crucially, this MUST be applied AFTER the contour/start-end geometry above: Tekla derives
    // the part's depth/plane axes from its actual geometry/profile orientation, and setting
    // Position first measures the offset against the operative's pre-geometry default orientation
    // — so the resulting plane ends up sitting at/inside the father's surface instead of offset
    // away from it (visible as the cutter's contour embedded in the material with no real cut).
    if (
      target.properties.TryGetValue("operative_position_depth", out var opDepth)
      && opDepth != null
      && Enum.TryParse<TSM.Position.DepthEnum>(opDepth.ToString(), out var opDepthEnum)
    )
      operativePart.Position.Depth = opDepthEnum;
    if (target.properties.TryGetValue("operative_position_depth_offset", out var opDOff) && opDOff != null)
      operativePart.Position.DepthOffset = System.Convert.ToDouble(opDOff);
    if (
      target.properties.TryGetValue("operative_position_plane", out var opPlane)
      && opPlane != null
      && Enum.TryParse<TSM.Position.PlaneEnum>(opPlane.ToString(), out var opPlaneEnum)
    )
      operativePart.Position.Plane = opPlaneEnum;
    if (target.properties.TryGetValue("operative_position_plane_offset", out var opPOff) && opPOff != null)
      operativePart.Position.PlaneOffset = System.Convert.ToDouble(opPOff);
    if (
      target.properties.TryGetValue("operative_position_rotation", out var opRot)
      && opRot != null
      && Enum.TryParse<TSM.Position.RotationEnum>(opRot.ToString(), out var opRotEnum)
    )
      operativePart.Position.Rotation = opRotEnum;
    if (target.properties.TryGetValue("operative_position_rotation_offset", out var opROff) && opROff != null)
      operativePart.Position.RotationOffset = System.Convert.ToDouble(opROff);

    // The operative part is a real, independent Part in the source model (its display is merely
    // suppressed because it's "consumed" by the boolean — see ModelObjectToSpeckleConverter's
    // GetBooleans()/OperativePart GUID check). Tekla's BooleanPart.Insert() silently rejects an
    // OperativePart that has no identity in the model database — it must be Insert()-ed as its
    // own object FIRST, exactly like the UI's "select an existing tool part" workflow, before it
    // can be attached as the cutting/adding tool of a BooleanPart.
    bool operativeInserted = operativePart.Insert();
    _logger.LogDebug(
      "      CreateBooleanPart operative pre-attach insert: type={Type} inserted={Inserted} identifier={Id}",
      operativePart.GetType().Name,
      operativeInserted,
      operativePart.Identifier
    );
    // Diagnostic only: re-read Position straight back off the inserted operative. If Tekla
    // normalizes/derives these from the final geometry on Insert() (rather than keeping exactly
    // what we assigned pre-insert), the values logged here will differ from "captured*" above —
    // that delta is the smoking gun for the remaining "some cuts float" mystery.
    _logger.LogDebug(
      "      CreateBooleanPart operative post-insert position (as read back): depth={Depth} depthOffset={DepthOffset} plane={Plane} planeOffset={PlaneOffset} rotation={Rotation} rotationOffset={RotationOffset}",
      operativePart.Position.Depth,
      operativePart.Position.DepthOffset,
      operativePart.Position.Plane,
      operativePart.Position.PlaneOffset,
      operativePart.Position.Rotation,
      operativePart.Position.RotationOffset
    );

    booleanPart.OperativePart = operativePart;

    // new streams: key is "boolean_type"; old streams: "type" key collided with TeklaObject.type so target.type itself holds the enum value
    string? boolTypeStr = null;
    if (target.properties.TryGetValue("boolean_type", out var bType) && bType != null)
      boolTypeStr = bType.ToString();
    else if (target.type == "BOOLEAN_CUT" || target.type == "BOOLEAN_ADD")
      boolTypeStr = target.type;
    if (boolTypeStr != null)
    {
#pragma warning disable CA1031
      try
      {
        booleanPart.Type = (TSM.BooleanPart.BooleanTypeEnum)
          Enum.Parse(typeof(TSM.BooleanPart.BooleanTypeEnum), boolTypeStr);
      }
      catch { }
#pragma warning restore CA1031
    }

    int operativeContourPointCount = operativePart switch
    {
      TSM.ContourPlate cpForLog => cpForLog.Contour?.ContourPoints?.Count ?? 0,
      TSM.PolyBeam pbForLog => pbForLog.Contour?.ContourPoints?.Count ?? 0,
      _ => 0
    };
    _logger.LogDebug(
      "      CreateBooleanPart pre-insert: operativeType={OperativeType} profile={Profile} booleanType={BooleanType} operativeContourPoints={ContourPoints} operativeStart={Start} operativeEnd={End}",
      operativePart.GetType().Name,
      operativePart.Profile.ProfileString,
      boolTypeStr,
      operativeContourPointCount,
      operativePart is TSM.Beam logBeam ? $"({logBeam.StartPoint.X}, {logBeam.StartPoint.Y}, {logBeam.StartPoint.Z})" : "n/a",
      operativePart is TSM.Beam logBeam2 ? $"({logBeam2.EndPoint.X}, {logBeam2.EndPoint.Y}, {logBeam2.EndPoint.Z})" : "n/a"
    );
    bool booleanInserted = booleanPart.Insert();
    LogInsertResult(target.type, booleanPart, booleanInserted);
    TeklaPartPropertyApplicator.ApplyUdas(booleanPart, target.properties);

    // The operative part only exists to give BooleanPart.OperativePart an identity to attach to
    // (see comment above) — once the boolean has consumed it, the standalone copy must be removed,
    // otherwise it lingers in the model as an extra, fully-visible duplicate part.
    if (booleanInserted)
    {
      bool operativeDeleted = operativePart.Delete();
      _logger.LogDebug(
        "      CreateBooleanPart post-insert operative cleanup: type={Type} deleted={Deleted} identifier={Id}",
        operativePart.GetType().Name,
        operativeDeleted,
        operativePart.Identifier
      );

      if (originApplicationId is not null)
      {
        TeklaOriginIdentifier.Set(booleanPart, originApplicationId, _logger);
      }
    }
  }

  /// <summary>
  /// Finds a BooleanPart already attached to <paramref name="fatherPart"/> that was inserted by a
  /// previous receive of the same source cut (identified by the origin applicationId UDA - see
  /// <see cref="TeklaOriginIdentifier"/>), so it can be replaced instead of duplicated.
  /// </summary>
  private static TSM.BooleanPart? FindExistingBooleanCut(TSM.Part fatherPart, string originApplicationId)
  {
    var booleans = fatherPart.GetBooleans();
    while (booleans.MoveNext())
    {
      if (
        booleans.Current is TSM.BooleanPart bp
        && TeklaOriginIdentifier.TryGet(bp, out string? existingOriginId)
        && existingOriginId == originApplicationId
      )
      {
        return bp;
      }
    }
    return null;
  }

  private void CreateCutPlane(TeklaObject target, TSM.ModelObject parent)
  {
    var cutPlane = new TSM.CutPlane();
    cutPlane.Father = parent;

    cutPlane.Plane = new TSM.Plane();
    if (target.properties.TryGetValue("plane_origin", out var originObj))
      cutPlane.Plane.Origin = MapPoint(originObj);
    if (target.properties.TryGetValue("plane_x", out var pxObj))
      cutPlane.Plane.AxisX = MapVector(pxObj);
    if (target.properties.TryGetValue("plane_y", out var pyObj))
      cutPlane.Plane.AxisY = MapVector(pyObj);

    bool cutPlaneInserted = cutPlane.Insert();
    LogInsertResult(target.type, cutPlane, cutPlaneInserted);
    TeklaPartPropertyApplicator.ApplyUdas(cutPlane, target.properties);
  }

  private void CreateEdgeChamfer(TeklaObject target, TSM.ModelObject parent)
  {
    var edgeChamfer = new TSM.EdgeChamfer();
    edgeChamfer.Father = parent;

    if (target.properties.TryGetValue("chamfer_type", out var ctObj) && ctObj != null
      && Enum.TryParse<TSM.Chamfer.ChamferTypeEnum>(ctObj.ToString(), out var ctEnum))
      edgeChamfer.Chamfer.Type = ctEnum;
    if (target.properties.TryGetValue("chamfer_x", out var cx))
      edgeChamfer.Chamfer.X = System.Convert.ToDouble(cx);
    if (target.properties.TryGetValue("chamfer_y", out var cy))
      edgeChamfer.Chamfer.Y = System.Convert.ToDouble(cy);
    if (target.properties.TryGetValue("first_bevel", out var fb))
      edgeChamfer.FirstBevelDimension = System.Convert.ToDouble(fb);
    if (target.properties.TryGetValue("second_bevel", out var sb))
      edgeChamfer.SecondBevelDimension = System.Convert.ToDouble(sb);
    if (target.properties.TryGetValue("first_chamfer_end_type", out var fcet) && fcet != null
      && Enum.TryParse<TSM.EdgeChamfer.ChamferEndTypeEnum>(fcet.ToString(), out var fcetEnum))
      edgeChamfer.FirstChamferEndType = fcetEnum;
    if (target.properties.TryGetValue("second_chamfer_end_type", out var scet) && scet != null
      && Enum.TryParse<TSM.EdgeChamfer.ChamferEndTypeEnum>(scet.ToString(), out var scetEnum))
      edgeChamfer.SecondChamferEndType = scetEnum;
    if (target.properties.TryGetValue("first_end", out var feObj))
      edgeChamfer.FirstEnd = MapPoint(feObj);
    if (target.properties.TryGetValue("second_end", out var seObj))
      edgeChamfer.SecondEnd = MapPoint(seObj);

    bool edgeChamferInserted = edgeChamfer.Insert();
    LogInsertResult(target.type, edgeChamfer, edgeChamferInserted);
    TeklaPartPropertyApplicator.ApplyUdas(edgeChamfer, target.properties);
  }

  private void CreateRebarSet(TeklaObject target, TSM.ModelObject parent)
  {
    TSM.Part? fatherPart = parent as TSM.Part;
    if (target.properties.TryGetValue("father_id", out var fId) && fId != null)
      fatherPart = _receiveCache.Get(fId.ToString()) as TSM.Part;
    if (fatherPart == null)
    {
      _logger.LogWarning("      CreateRebarSet: father part could not be resolved (father_id={FatherId}, parent={ParentType})", fId?.ToString(), parent.GetType().Name);
      return;
    }

    var rebarSet = new TSM.RebarSet();
    rebarSet.FatherPart = fatherPart;

    if (target.properties.TryGetValue("layer_order_number", out var lon) && lon != null)
      rebarSet.LayerOrderNumber = System.Convert.ToInt32(lon);

    if (target.properties.TryGetValue("rebar_size", out var size) && size != null)
      rebarSet.RebarProperties.Size = size.ToString();
    if (target.properties.TryGetValue("rebar_grade", out var grade) && grade != null)
      rebarSet.RebarProperties.Grade = grade.ToString();
    if (target.properties.TryGetValue("rebar_name", out var rname) && rname != null)
      rebarSet.RebarProperties.Name = rname.ToString();
    if (target.properties.TryGetValue("rebar_class", out var rcls) && rcls != null)
      rebarSet.RebarProperties.Class = System.Convert.ToInt32(rcls);
    if (target.properties.TryGetValue("bending_radius", out var br) && br != null)
      rebarSet.RebarProperties.BendingRadius = System.Convert.ToDouble(br);

    // BarOrientation — without it Tekla derives a default bar facing from the guideline
    // geometry, which can point each bar's cross-section the wrong way and make adjacent
    // sets overlap (mirrors the SingleRebar/RebarGroup offset fix above).
    if (
      target.properties.TryGetValue("bar_orientation_start", out var boStart) && boStart != null
      && target.properties.TryGetValue("bar_orientation_end", out var boEnd) && boEnd != null
    )
    {
      rebarSet.BarOrientation = new TG.LineSegment(MapPoint(boStart), MapPoint(boEnd));
    }

    if (target.properties.TryGetValue("leg_faces", out var lfObj) && lfObj is IEnumerable<object> legFacesEnum)
    {
      foreach (var lfRaw in legFacesEnum)
      {
        if (lfRaw is not IDictionary<string, object> lfDict)
          continue;
        var legFace = new TSM.RebarLegFace();
        if (lfDict.TryGetValue("additional_offset", out var ao))
          legFace.AdditonalOffset = System.Convert.ToDouble(ao);
        if (lfDict.TryGetValue("layer_order_number", out var lfLon))
          legFace.LayerOrderNumber = System.Convert.ToInt32(lfLon);
        if (lfDict.TryGetValue("reversed", out var rev))
          legFace.Reversed = System.Convert.ToBoolean(rev);
        if (lfDict.TryGetValue("contour_points", out var cpts) && cpts is IEnumerable<object> cptsList)
        {
          var ptCoords = cptsList.Select(p => System.Convert.ToDouble(p)).ToList();
          for (int i = 0; i * 3 + 2 < ptCoords.Count; i++)
          {
            legFace.Contour.AddContourPoint(
              new TSM.ContourPoint(new TG.Point(ptCoords[i * 3], ptCoords[i * 3 + 1], ptCoords[i * 3 + 2]), null)
            );
          }
        }
        rebarSet.LegFaces.Add(legFace);
      }
    }

    if (target.properties.TryGetValue("guidelines", out var glObj) && glObj is IEnumerable<object> guidelinesEnum)
    {
      foreach (var glRaw in guidelinesEnum)
      {
        if (glRaw is not IDictionary<string, object> glDict)
          continue;
        var guideline = new TSM.RebarGuideline();
        if (glDict.TryGetValue("follow_edges", out var fe))
          guideline.FollowEdges = System.Convert.ToBoolean(fe);
        if (glDict.TryGetValue("curve_points", out var cpts) && cpts is IEnumerable<object> cptsList)
        {
          var ptCoords = cptsList.Select(p => System.Convert.ToDouble(p)).ToList();
          for (int i = 0; i * 3 + 2 < ptCoords.Count; i++)
          {
            guideline.Curve.AddContourPoint(
              new TSM.ContourPoint(new TG.Point(ptCoords[i * 3], ptCoords[i * 3 + 1], ptCoords[i * 3 + 2]), null)
            );
          }
        }
        var resolvedSpacing = BuildRebarSpacing(glDict);
        if (resolvedSpacing != null)
          guideline.Spacing = resolvedSpacing;
        rebarSet.Guidelines.Add(guideline);
      }
    }

    _logger.LogDebug(
      "      CreateRebarSet pre-insert: legFaces={LegFaces} guidelines={Guidelines} size={Size} grade={Grade}",
      rebarSet.LegFaces.Count,
      rebarSet.Guidelines.Count,
      rebarSet.RebarProperties.Size,
      rebarSet.RebarProperties.Grade
    );
    bool rebarSetInserted = rebarSet.Insert();
    LogInsertResult(target.type, rebarSet, rebarSetInserted);
    TeklaPartPropertyApplicator.ApplyUdas(rebarSet, target.properties);
  }

  // RebarSpacing can't simply be default-constructed and have its properties set afterwards —
  // Tekla expects it to be built via the static Create(...) factories matching the spacing type
  // (number-of-bars, exact-spacings list, or a single distance), each requiring its own data.
  private TSM.RebarSpacing? BuildRebarSpacing(IDictionary<string, object> spacingData)
  {
    var type = TSM.RebarSpacing.SpacingType.UNDEFINED;
    if (
      spacingData.TryGetValue("spacing_type", out var st)
      && st != null
      && Enum.TryParse<TSM.RebarSpacing.SpacingType>(st.ToString(), out var typeEnum)
    )
    {
      type = typeEnum;
    }

    if (type == TSM.RebarSpacing.SpacingType.UNDEFINED)
      return null;

    bool startAutomatic = spacingData.TryGetValue("spacing_start_offset_automatic", out var sa) && sa != null
      && System.Convert.ToBoolean(sa);
    double startOffsetValue = spacingData.TryGetValue("spacing_start_offset", out var so) && so != null
      ? System.Convert.ToDouble(so)
      : 0.0;
    bool endAutomatic = spacingData.TryGetValue("spacing_end_offset_automatic", out var ea) && ea != null
      && System.Convert.ToBoolean(ea);
    double endOffsetValue = spacingData.TryGetValue("spacing_end_offset", out var eo) && eo != null
      ? System.Convert.ToDouble(eo)
      : 0.0;

    var startOffset = new TSM.RebarSpacing.Offset(startAutomatic, startOffsetValue);
    var endOffset = new TSM.RebarSpacing.Offset(endAutomatic, endOffsetValue);

    switch (type)
    {
      case TSM.RebarSpacing.SpacingType.NUMBER_BARS:
        int numberOfBars = spacingData.TryGetValue("spacing_bars", out var bars) && bars != null
          ? System.Convert.ToInt32(bars)
          : 0;
        return TSM.RebarSpacing.Create(startOffset, endOffset, numberOfBars);

      case TSM.RebarSpacing.SpacingType.EXACT_SPACINGS:
        var elements = new List<TSM.RebarSpacing.ExactSpacing.Element>();
        if (
          spacingData.TryGetValue("spacing_exact_elements", out var elementsObj)
          && elementsObj is IEnumerable<object> elementsEnum
        )
        {
          var values = elementsEnum.Select(System.Convert.ToDouble).ToList();
          for (int i = 0; i * 2 + 1 < values.Count; i++)
          {
            elements.Add(new TSM.RebarSpacing.ExactSpacing.Element((int)values[i * 2], values[i * 2 + 1]));
          }
        }
        return TSM.RebarSpacing.Create(startOffset, endOffset, elements, out _);

      default:
        // EXACT, TARGET, and the EXACT_FLEXIBLE_* variants are all defined by a single distance —
        // TARGET uses the target space, the rest use the exact space.
        double distance =
          type == TSM.RebarSpacing.SpacingType.TARGET
            ? (spacingData.TryGetValue("spacing_target", out var tgt) && tgt != null ? System.Convert.ToDouble(tgt) : 0.0)
            : (spacingData.TryGetValue("spacing_exact", out var exact) && exact != null ? System.Convert.ToDouble(exact) : 0.0);
        return TSM.RebarSpacing.Create(type, startOffset, endOffset, distance);
    }
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
