using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace Speckle.Connectors.Revit.HostApp;

/// <summary>
/// One parent–child suppression rule.
/// </summary>
/// <param name="Name">Human-readable description used for diagnostics.</param>
/// <param name="IsChild">
///   Returns <c>true</c> when the evaluated element is a child whose parent is already
///   represented in the parent-id set.
/// </param>
public sealed record RevitParentChildRule(
  string Name,
  Func<Element, ISet<ElementId>, Document, bool> IsChild
);

/// <summary>
/// Central mapping of Revit parent–child element relationships used to deduplicate the send selection.
/// When a parent element is present in the selection its registered children are suppressed, because
/// the parent converter already produces those child objects internally.
/// <para>
/// Add a new <see cref="RevitParentChildRule"/> entry to <see cref="All"/> whenever a Revit element
/// type should be filtered out of the selection when its logical owner is also selected.
/// </para>
/// </summary>
public static class RevitParentChildRules
{
  /// <summary>All active parent–child rules, evaluated in order.</summary>
  public static readonly IReadOnlyList<RevitParentChildRule> All =
  [
    // Curtain wall: mullions are produced by the curtain-wall converter.
    new RevitParentChildRule(
      "Mullion → CurtainWall",
      (el, ids, _) => el is Mullion { Host: not null } m && ids.Contains(m.Host.Id)
    ),

    // Curtain wall: panels are produced by the curtain-wall converter.
    // Exception: when the host is a CurtainSystem the panel must be sent on its own.
    // See CNX-1884: https://linear.app/speckle/issue/CNX-1884
    new RevitParentChildRule(
      "Panel → CurtainWall (not CurtainSystem) [CNX-1884]",
      (el, ids, doc) =>
        el is Panel { Host: not null } p
        && ids.Contains(p.Host.Id)
        && doc.GetElement(p.Host.Id) is not CurtainSystem
    ),

    // Curtain wall: embedded FamilyInstance panels (custom curtain panels) live inside a curtain wall.
    new RevitParentChildRule(
      "CurtainWall embedded FamilyInstance → CurtainWall",
      (el, ids, doc) =>
        el is FamilyInstance { Host: not null } f
        && doc.GetElement(f.Host.Id) is Wall { CurtainGrid: not null }
        && ids.Contains(f.Host.Id)
    ),

    // Stacked walls: when elements come from a view the API returns both the StackedWall parent and
    // each member wall separately. Via selection or category filter only the members are returned.
    // The stacked-wall converter includes all members, so member walls must be suppressed.
    // See CNX-851: https://linear.app/speckle/issue/CNX-851
    new RevitParentChildRule(
      "StackedWallMember → StackedWall [CNX-851]",
      (el, ids, _) =>
        el is Wall { IsStackedWallMember: true } w && ids.Contains(w.StackedWallOwnerId)
    ),

    // Railings: the railing converter includes TopRail as a nested child element.
    // Suppress the standalone TopRail when the parent railing is also selected.
    // TODO: Evaluate whether HandRail (also inherits ContinuousRail) needs the same treatment.
    new RevitParentChildRule(
      "TopRail → Railing",
      (el, ids, doc) =>
        el is TopRail tr
        && doc.GetElement(tr.HostRailingId) is Railing r
        && ids.Contains(r.Id)
    ),
  ];
}
