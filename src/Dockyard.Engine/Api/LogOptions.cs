namespace Dockyard.Engine;

public sealed record LogOptions(bool Follow = true, int? Tail = 1000, bool Tty = false);
