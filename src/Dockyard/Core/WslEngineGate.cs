using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Dockyard.Core;

static partial class WslEngineGate
{
    public const string TokenPath = "/etc/dockyard/windows.token";

    // What `[ -d /sys/class/net/loopback0 ] && echo mirrored || ip -4 -o addr show eth0` prints in the distro: with mirrored
    // networking the agent is on the loopback Windows shares, with NAT on WSL's own address; nothing gives null.
    public static IPAddress? AddressFromIp(string output) =>
        output.Trim() == "mirrored" ? IPAddress.Loopback
        : InetAddress().Match(output) is { Success: true } m && IPAddress.TryParse(m.Groups["a"].Value, out var a) ? a : null;

    // In this user's local app data, which other users of the PC cannot read; made once and kept, so a tray that starts
    // while an engine runs still gets in.
    public static string Token(string file)
    {
        if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: 64 } kept && kept.All(char.IsAsciiHexDigitLower)) return kept;
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, token);
        return token;
    }

    [GeneratedRegex(@"\binet (?<a>\d+\.\d+\.\d+\.\d+)/")]
    private static partial Regex InetAddress();
}
