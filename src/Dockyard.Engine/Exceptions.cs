using System.Net;
using Dockyard.Engine.Transport;

namespace Dockyard.Engine;

public class DockerException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed class NotFoundException(string message) : DockerException(HttpStatusCode.NotFound, message);

public sealed class ConflictException(string message) : DockerException(HttpStatusCode.Conflict, message);

public sealed class EngineUnavailableException(DockerEndpoint endpoint, Exception inner)
    : Exception($"Docker engine at {endpoint.Uri} is not reachable: {inner.Message}", inner)
{
    public DockerEndpoint Endpoint { get; } = endpoint;
}

public sealed class DockerContextException(string message) : Exception(message);
