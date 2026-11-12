using Dockyard.Engine.Transport;

namespace Dockyard.Engine;

public sealed class EngineUnavailableException(DockerEndpoint endpoint, Exception inner)
    : Exception($"Docker engine at {endpoint.Uri} is not reachable: {inner.Message}", inner)
{
    public DockerEndpoint Endpoint { get; } = endpoint;
}
