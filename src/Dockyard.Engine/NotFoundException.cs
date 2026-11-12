using System.Net;

namespace Dockyard.Engine;

public sealed class NotFoundException(string message) : DockerException(HttpStatusCode.NotFound, message);
