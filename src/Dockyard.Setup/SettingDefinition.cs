namespace Dockyard.Setup;

// A Toggle is "on" or "off"; a Choice one of Choices; an EngineFile the engine's daemon.json.
public sealed record SettingDefinition(string Key, string Title, string Description, SettingKind Kind, IReadOnlyList<string>? Choices, bool LinuxEngineOnly, bool NeedsAdmin);

public enum SettingKind { Toggle, Choice, Text, EngineFile }
