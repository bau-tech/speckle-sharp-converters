using System.Diagnostics.CodeAnalysis;
using Autodesk.Revit.DB.ExtensibleStorage;
using Microsoft.Extensions.Logging;

namespace Speckle.Converters.RevitShared.Helpers;

/// <summary>
/// Persists the ORIGIN Speckle applicationId (the id an element carried the first time it was ever
/// received, e.g. a Tekla part's GUID) onto the Revit element that was created for it, so a later
/// send can re-emit that same id instead of a fresh <c>Element.UniqueId</c>. Without this, an element
/// that round-trips through Revit loses its original cross-app identity and downstream apps can't
/// recognize it as the same real-world object on a subsequent receive.
/// </summary>
public static class OriginApplicationIdSchema
{
  private static readonly Guid s_schemaGuid = new("D9A6B0C2-6F0D-4E1F-9B7E-6E5A6C6B0F41");
  private const string FIELD_NAME = "OriginApplicationId";

  private static Schema GetSchema()
  {
    Schema schema = Schema.Lookup(s_schemaGuid);
    if (schema != null)
    {
      return schema;
    }

    using SchemaBuilder builder = new(s_schemaGuid);
    builder.SetSchemaName("SpeckleOriginApplicationId");
    builder.AddSimpleField(FIELD_NAME, typeof(string));
    return builder.Finish();
  }

  /// <summary>
  /// Fails open (logged, never throws) - a freshly-created element of some categories (observed for
  /// Structural Foundation FamilyInstances) can throw here if it hasn't been regenerated yet; losing
  /// the origin-id stamp (and with it, update/delete tracking for this one element on a later
  /// receive) is far better than losing the element's native representation entirely by letting this
  /// bubble up and force a DirectShape fallback.
  /// </summary>
  public static void TrySet(DB.Element element, string originApplicationId, ILogger? logger = null)
  {
    try
    {
      Entity entity = new(GetSchema());
      entity.Set(FIELD_NAME, originApplicationId);
      element.SetEntity(entity);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      // Deliberately don't touch `element` here (not even .Id) - if SetEntity() above threw because
      // the element reference itself is invalid/stale, touching it again in the catch would throw a
      // SECOND time and escape this catch entirely, which is exactly what happened before this guard
      // existed (observed for freshly-created Structural Foundation instances).
      logger?.LogWarning(
        ex,
        "Could not stamp the origin-id UDA ({OriginApplicationId}) on an element; it won't be update/delete-tracked on a later receive.",
        originApplicationId
      );
    }
  }

  public static bool TryGet(DB.Element element, [NotNullWhen(true)] out string? originApplicationId)
  {
    Entity entity = element.GetEntity(GetSchema());
    if (!entity.IsValid())
    {
      originApplicationId = null;
      return false;
    }

    originApplicationId = entity.Get<string>(FIELD_NAME);
    return !string.IsNullOrEmpty(originApplicationId);
  }
}
