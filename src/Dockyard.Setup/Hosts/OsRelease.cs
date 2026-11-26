namespace Dockyard.Hosts;

sealed record OsRelease(string Id, IReadOnlyList<string> IdLike, string? Codename)
{
    public static OsRelease Parse(string text)
    {
        var values = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('='))
            .Select(l => (Key: l[..l.IndexOf('=')], Value: l[(l.IndexOf('=') + 1)..].Trim('"', '\'')))
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Last().Value);
        return new OsRelease(
            values.GetValueOrDefault("ID", "linux"),
            values.GetValueOrDefault("ID_LIKE", "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
            values.GetValueOrDefault("UBUNTU_CODENAME") ?? values.GetValueOrDefault("VERSION_CODENAME"));
    }

    public bool HasPackages => PackageManager is "apt" or "dnf";

    public string? PackageManager => new[] { Id }.Concat(IdLike).Select(id => id switch
    {
        "ubuntu" or "debian" => "apt",
        "fedora" or "rhel" or "centos" => "dnf",
        _ => null,
    }).FirstOrDefault(p => p is not null);
}
