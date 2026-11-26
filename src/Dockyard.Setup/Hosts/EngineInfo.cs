namespace Dockyard.Hosts;

sealed record EngineInfo(string Id, string Name, string Title, string Subtitle, string Context, string Endpoint, string? ComponentId = null);
