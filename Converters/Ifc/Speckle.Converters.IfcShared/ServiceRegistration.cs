using Microsoft.Extensions.DependencyInjection;
using Speckle.Connectors.Common.Operations;
using Speckle.Converters.IfcShared.BlobRetrieval;
using Speckle.Converters.IfcShared.Enrichment;

namespace Speckle.Converters.IfcShared;

public static class ServiceRegistration
{
  /// <summary>
  /// Registers IFC native-reconstruction support, including overriding the default
  /// <see cref="NoOpReceivedObjectEnricher"/> (see <c>Speckle.Connectors.Common.ContainerRegistration.AddConnectors</c>)
  /// with the real <see cref="RevitNativeSchemaEnricher"/>. Must be called AFTER <c>AddConnectors()</c>
  /// for the override to win (last registration wins for a given service type) - only the Revit
  /// connector calls this; every other connector keeps the no-op.
  /// </summary>
  public static IServiceCollection AddIfcNativeReconstruction(this IServiceCollection serviceCollection)
  {
    serviceCollection.AddScoped<IIfcSourceResolver, IfcSourceResolver>();
    serviceCollection.AddScoped<IIfcBlobDownloader, IfcBlobDownloader>();
    serviceCollection.AddScoped<IIfcGlobalIdScanner, IfcGlobalIdScanner>();
    serviceCollection.AddScoped<IReceivedObjectEnricher, RevitNativeSchemaEnricher>();
    return serviceCollection;
  }
}
