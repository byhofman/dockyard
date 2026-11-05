using System.Security.Cryptography;

namespace Dockyard.Core;

static class Checksums
{
    // SHA256SUMS as sha256sum writes it: "<hash>  <name>".
    public static string? Expected(string sums, string fileName) => sums.Split('\n')
        .Select(line => line.Trim().Split(' ', 2, StringSplitOptions.TrimEntries))
        .Where(parts => parts.Length == 2 && parts[1].TrimStart('*') == fileName)
        .Select(parts => parts[0])
        .FirstOrDefault();

    public static void Verify(string sums, string file)
    {
        var name = Path.GetFileName(file);
        var expected = Expected(sums, name) ?? throw new InvalidOperationException($"SHA256SUMS does not list {name}");
        using var stream = File.OpenRead(file);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Checksum mismatch for {name}");
    }
}
