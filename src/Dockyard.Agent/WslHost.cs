using System.Net;

namespace Dockyard.Agent;

static class WslHost
{
    // Mirrored networking gives WSL an interface named loopback0, and 127.0.0.1 then reaches Windows. With NAT only the
    // gateway reaches Windows, and only services that listen on more than loopback and that its firewall lets through.
    public static IPAddress Address(bool mirrored, string procNetRoute) =>
        mirrored ? IPAddress.Loopback : DefaultGateway(procNetRoute) ?? IPAddress.Loopback;

    public static IPAddress? DefaultGateway(string procNetRoute)
    {
        foreach (var line in procNetRoute.Split('\n').Skip(1))
        {
            var fields = line.Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 2 && fields[1] == "00000000" && uint.TryParse(fields[2], System.Globalization.NumberStyles.HexNumber, null, out var gateway))
                return new IPAddress(gateway);
        }
        return null;
    }
}
