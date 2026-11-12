using Dockyard.Dashboard.Shell;

namespace Dockyard.Dashboard.Features.Resources;

sealed record VolumeRow(string Name, string Driver, string Mountpoint, long? Size, IReadOnlyList<string> UsedBy, IReadOnlyDictionary<string, string> Labels)
{
    public bool IsAnonymous => Name.Length == 64 && Name.All(char.IsAsciiHexDigit);
    public string DisplayName => IsAnonymous ? Name[..12] + "…" : Name;
    public string SizeText => Size is { } s && s >= 0 ? Format.Bytes(s) : "";
    public bool InUse => UsedBy.Count > 0;
    public bool CanRemove => !InUse;
    public string UsageText => UsedBy.Count == 0 ? "unused" : string.Join(", ", UsedBy);
}
