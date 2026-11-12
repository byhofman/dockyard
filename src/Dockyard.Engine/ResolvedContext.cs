using Dockyard.Engine.Transport;

namespace Dockyard.Engine;

public sealed record ResolvedContext(string Name, DockerEndpoint Endpoint);
