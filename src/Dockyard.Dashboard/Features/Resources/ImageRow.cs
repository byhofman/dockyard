using Dockyard.Dashboard.Shell;

namespace Dockyard.Dashboard.Features.Resources;

sealed record ImageRow(string Id, string Reference, long Size, DateTimeOffset Created, IReadOnlyList<string> Tags, IReadOnlyList<string> UsedBy)
{
    public string ShortId => Id.StartsWith("sha256:", StringComparison.Ordinal) ? Id[7..19] : Id[..Math.Min(12, Id.Length)];
    public string SizeText => Format.Bytes(Size);
    public string CreatedText => Format.Age(Created, DateTimeOffset.UtcNow);
    public bool InUse => UsedBy.Count > 0;
    public string UsageText => UsedBy.Count == 0 ? "unused" : UsedBy.Count == 1 ? "1 container" : $"{UsedBy.Count} containers";
    public bool IsDangling => Tags.Count == 0;
}
