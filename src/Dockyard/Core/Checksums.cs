using System.Security.Cryptography;

namespace Dockyard.Core;

static class Checksums
{
    // SHA256SUMS as sha256sum writes it: "<hash>  <name>".
    static IEnumerable<(string Hash, string Name)> Entries(string sums) => sums.Split('\n')
        .Select(line => line.Trim().Split(' ', 2, StringSplitOptions.TrimEntries))
        .Where(parts => parts.Length == 2)
        .Select(parts => (parts[0], parts[1].TrimStart('*')));

    public static IEnumerable<string> Names(string sums) => Entries(sums).Select(e => e.Name);

    public static string? Expected(string sums, string fileName) =>
        Entries(sums).Where(e => e.Name == fileName).Select(e => e.Hash).FirstOrDefault();

    public static void Verify(string sums, string file)
    {
        var name = Path.GetFileName(file);
        VerifyHash(file, Expected(sums, name) ?? throw new InvalidOperationException($"SHA256SUMS does not list {name}"), name);
    }

    public static void VerifyHash(string file, string expected, string? name = null)
    {
        using var stream = File.OpenRead(file);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Checksum mismatch for {name ?? Path.GetFileName(file)}");
    }
}
