namespace Dockyard.Engine;

public readonly record struct LogLine(LogStream Stream, DateTimeOffset? Time, string Text);
