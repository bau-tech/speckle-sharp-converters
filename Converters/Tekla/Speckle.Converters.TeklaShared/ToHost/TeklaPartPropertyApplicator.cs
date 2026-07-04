using Speckle.Objects.Data;

namespace Speckle.Converters.TeklaShared.ToHost;

/// <summary>
/// Centralises property application for all TSM.Part-derived objects (Beam, PolyBeam, ContourPlate, etc.)
/// so that every converter gets identical position, numbering, phase, deformation and UDA handling.
/// </summary>
internal static class TeklaPartPropertyApplicator
{
  /// <summary>Applies all common Part properties from a TeklaObject to a freshly-created TSM.Part.</summary>
  public static void Apply(TSM.Part part, TeklaObject target)
  {
    if (target.name is not null)
      part.Name = target.name;

    var props = target.properties;
    if (props is null)
      return;

    // ── Core ─────────────────────────────────────────────────────────────
    if (props.TryGetValue("profile", out var prof) && prof is not null)
      part.Profile.ProfileString = prof.ToString();
    if (props.TryGetValue("material", out var mat) && mat is not null)
      part.Material.MaterialString = mat.ToString();
    if (props.TryGetValue("class", out var cls) && cls is not null)
      part.Class = cls.ToString();
    if (props.TryGetValue("finish", out var fin) && fin is not null)
      part.Finish = fin.ToString();

    // ── Position ─────────────────────────────────────────────────────────
    if (props.TryGetValue("position_depth", out var depth) && depth is not null
        && Enum.TryParse<TSM.Position.DepthEnum>(depth.ToString(), out var depthEnum))
      part.Position.Depth = depthEnum;
    if (props.TryGetValue("position_depth_offset", out var dOff))
      part.Position.DepthOffset = System.Convert.ToDouble(dOff);
    if (props.TryGetValue("position_plane", out var plane) && plane is not null
        && Enum.TryParse<TSM.Position.PlaneEnum>(plane.ToString(), out var planeEnum))
      part.Position.Plane = planeEnum;
    if (props.TryGetValue("position_plane_offset", out var pOff) && pOff is not null)
      part.Position.PlaneOffset = System.Convert.ToDouble(pOff!);
    if (props.TryGetValue("position_rotation", out var rot) && rot is not null
        && Enum.TryParse<TSM.Position.RotationEnum>(rot.ToString(), out var rotEnum))
      part.Position.Rotation = rotEnum;
    if (props.TryGetValue("position_rotation_offset", out var rOff) && rOff is not null)
      part.Position.RotationOffset = System.Convert.ToDouble(rOff!);

    // ── Numbering ────────────────────────────────────────────────────────
    if (props.TryGetValue("part_prefix", out var pPre) && pPre is not null)
      part.PartNumber.Prefix = pPre.ToString();
    if (props.TryGetValue("part_start_no", out var pStart))
      part.PartNumber.StartNumber = System.Convert.ToInt32(pStart);
    if (props.TryGetValue("assembly_prefix", out var aPre) && aPre is not null)
      part.AssemblyNumber.Prefix = aPre.ToString();
    if (props.TryGetValue("assembly_start_no", out var aStart))
      part.AssemblyNumber.StartNumber = System.Convert.ToInt32(aStart);

    // ── Phase ────────────────────────────────────────────────────────────
    if (props.TryGetValue("phase", out var phaseNum))
    {
      try
      {
        var phase = new TSM.Phase { PhaseNumber = System.Convert.ToInt32(phaseNum) };
        // Restore phase name when it was explicitly captured on send
        if (props.TryGetValue("phase_name", out var phaseName) && phaseName is not null)
          phase.PhaseName = phaseName.ToString();
        part.SetPhase(phase);
      }
#pragma warning disable CA1031
      catch { /* Phase number may not exist in the target model — skip silently */ }
#pragma warning restore CA1031
    }

    // ── Beam-specific ────────────────────────────────────────────────────
    if (part is TSM.Beam beam)
    {
      // Note: Deformation properties (Prelength, Twist, Camber) need the correct
      // Tekla API property names confirmed against the installed version before adding.

      // End offsets (Beam-specific)
      if (props.TryGetValue(nameof(beam.StartPointOffset), out var sOffObj)
          && sOffObj is IEnumerable<object> sOffList)
      {
        var d = sOffList.Select(i => System.Convert.ToDouble(i)).ToList();
        beam.StartPointOffset.Dx = d[0];
        beam.StartPointOffset.Dy = d[1];
        beam.StartPointOffset.Dz = d[2];
      }
      if (props.TryGetValue(nameof(beam.EndPointOffset), out var eOffObj)
          && eOffObj is IEnumerable<object> eOffList)
      {
        var d = eOffList.Select(i => System.Convert.ToDouble(i)).ToList();
        beam.EndPointOffset.Dx = d[0];
        beam.EndPointOffset.Dy = d[1];
        beam.EndPointOffset.Dz = d[2];
      }
    }

    // ── User-Defined Attributes ──────────────────────────────────────────
    ApplyUdas(part, props);
  }

  /// <summary>
  /// Writes back all User-Defined Attributes from the "User Defined Attributes" property bag
  /// to any TSM.ModelObject (parts AND sub-components).
  /// Tekla's UDA API uses type-specific overloads of SetUserProperty; we dispatch by value type.
  /// </summary>
  public static void ApplyUdas(TSM.ModelObject modelObject, Dictionary<string, object?>? props)
  {
    if (props is null) return;
    if (!props.TryGetValue("User Defined Attributes", out var udasObj)) return;
    if (udasObj is not IDictionary<string, object?> udas) return;

    foreach (var kvp in udas)
    {
      if (kvp.Value is null) continue;
      try
      {
        switch (kvp.Value)
        {
          case int i:
            modelObject.SetUserProperty(kvp.Key, i);
            break;
          case long l:
            modelObject.SetUserProperty(kvp.Key, (int)l);
            break;
          case double d:
            modelObject.SetUserProperty(kvp.Key, d);
            break;
          case float f:
            modelObject.SetUserProperty(kvp.Key, (double)f);
            break;
          case string s:
            modelObject.SetUserProperty(kvp.Key, s);
            break;
          default:
            modelObject.SetUserProperty(kvp.Key, kvp.Value.ToString());
            break;
        }
      }
#pragma warning disable CA1031
      catch { /* Skip individual UDAs that fail — don't block the rest */ }
#pragma warning restore CA1031
    }
  }
}
