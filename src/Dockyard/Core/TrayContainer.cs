namespace Dockyard.Core;

sealed record TrayContainer(string Id, string Name, string? Project, bool Running, int? WebPort);
