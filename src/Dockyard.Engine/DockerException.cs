using System.Net;

namespace Dockyard.Engine;

public class DockerException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
