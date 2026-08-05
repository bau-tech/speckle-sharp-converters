using Speckle.Sdk.Models;

namespace Speckle.Connectors.Common.Operations;

/// <summary>
/// Optional hook to enrich/mutate the deserialized root object tree before it reaches the host
/// application's <see cref="Speckle.Connectors.Common.Builders.IHostObjectBuilder"/>, given full
/// receive context (<see cref="ReceiveInfo"/> - <c>Build</c> itself only receives the object tree and
/// project/model names, not <c>ProjectId</c>/<c>ModelId</c>/<c>SelectedVersionId</c>/<c>Account</c>).
/// </summary>
/// <remarks>
/// Default registration is <see cref="NoOpReceivedObjectEnricher"/> (see
/// <see cref="ContainerRegistration.AddConnectors"/>) - every connector's receive behavior is
/// unchanged unless it explicitly overrides the registration with its own implementation. Introduced
/// for the IFC native-reconstruction feature (see Converters/Ifc/Speckle.Converters.IfcShared),
/// registered only by the Revit connector - Tekla/AutoCAD/Rhino/etc. keep the no-op.
/// </remarks>
public interface IReceivedObjectEnricher
{
  Task<Base> Enrich(Base rootObject, ReceiveInfo receiveInfo, CancellationToken cancellationToken);
}

public sealed class NoOpReceivedObjectEnricher : IReceivedObjectEnricher
{
  public Task<Base> Enrich(Base rootObject, ReceiveInfo receiveInfo, CancellationToken cancellationToken) =>
    Task.FromResult(rootObject);
}
