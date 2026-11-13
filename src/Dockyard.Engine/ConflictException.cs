using System.Net;

namespace Dockyard.Engine;

public sealed class ConflictException(string message) : DockerException(HttpStatusCode.Conflict, message);
