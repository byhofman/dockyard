namespace Dockyard.Setup;

// A Toggle is "on" or "off"; a Choice one of Choices; an EngineFile the engine's daemon.json. Group is the heading a front end
// shows it under, and a group's settings come one after the other.
public sealed record SettingDefinition(string Key, string Title, string Description, SettingKind Kind, IReadOnlyList<string>? Choices, bool LinuxEngineOnly, bool NeedsAdmin, string Group = "");

public enum SettingKind { Toggle, Choice, Text, EngineFile }
