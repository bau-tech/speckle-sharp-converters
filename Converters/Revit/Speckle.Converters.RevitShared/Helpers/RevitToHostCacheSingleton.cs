namespace Speckle.Converters.RevitShared.Helpers;

public class RevitToHostCacheSingleton
{
  /// <summary>
  /// POC: Not sure is there a way to create it on "RevitHostObjectBuilder" with a scope instead singleton. For now we fill this dictionary and clear it on "RevitHostObjectBuilder".
  /// Map extracted by revit material baker to be able to use it in converter.
  /// This is needed because we cannot set materials for meshes in connector.
  /// They needed to be set while creating "TessellatedFace".
  /// </summary>
  public Dictionary<string, DB.ElementId> MaterialsByObjectId { get; } = new();

  /// <summary>
  /// Maps InstanceDefinitionProxy.applicationId to the created Revit Family.
  /// Populated by RevitFamilyBaker during receive operations.
  /// </summary>
  public Dictionary<string, DB.Family> FamiliesByDefinitionId { get; } = new();

  /// <summary>
  /// Maps InstanceDefinitionProxy.applicationId to the activated FamilySymbol (for placement).
  /// Populated by RevitFamilyBaker during receive operations.
  /// </summary>
  public Dictionary<string, DB.FamilySymbol> SymbolsByDefinitionId { get; } = new();

  /// <summary>
  /// Maps the source RevitObject's applicationId (the original sender-side Element.UniqueId) to the
  /// natively-created DB.Element. Populated by category-specific NativeRevit ToHost converters
  /// (Beam, Column, Wall, Floor, ...) so that dependent elements (e.g. Openings) can resolve their
  /// host element during the same receive operation.
  /// </summary>
  public Dictionary<string, DB.Element> ReceivedElementsByApplicationId { get; } = new();

  public void Clear()
  {
    MaterialsByObjectId.Clear();
    FamiliesByDefinitionId.Clear();
    SymbolsByDefinitionId.Clear();
    ReceivedElementsByApplicationId.Clear();
  }
}
