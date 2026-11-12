using System.Net;

namespace Dockyard.Agent;

// Addresses that only exist inside the engine's Linux, so they cannot clash with anything on the user's networks.
static class GuestAddresses
{
    public static readonly IPAddress Dns = IPAddress.Parse("198.19.250.1");
    public static readonly IPAddress Host = IPAddress.Parse("198.19.250.254");
    public const string Subnet = "198.19.250.0/24";
    public const int HostProxyPort = 2378;
    public const int DomainHttpPort = 2380;
    public const int DomainHttpsPort = 2381;
    public const int RelayHttpPort = 2382;
    public const int RelayHttpsPort = 2383;
    public const int RelayDnsPort = 2353;
    public const int ContainerDnsPort = 2354;

    // Where the host's browser finds domains where it cannot use the engine's own address: all of 127/8 is loopback,
    // and an address nothing else uses cannot clash with a server on 127.0.0.1:80, such as another Docker app's.
    public static readonly IPAddress HostLoopback = IPAddress.Parse("127.77.0.1");

    static readonly HashSet<string> HostNames = ["host.docker.internal", "gateway.docker.internal"];

    public static IPAddress? Resolve(string name) => HostNames.Contains(name) ? Host : null;

    // What host.docker.internal never reaches on the host: Docker's own TCP ports and Dockyard's, whose services are for
    // the host and the tray, not for any image a container runs.
    public static readonly IReadOnlySet<int> RefusedOnHost = new HashSet<int>
    {
        2375, 2376, 2377, HostProxyPort, 2379, DomainHttpPort, DomainHttpsPort, RelayHttpPort, RelayHttpsPort, RelayDnsPort, ContainerDnsPort,
    };
}
