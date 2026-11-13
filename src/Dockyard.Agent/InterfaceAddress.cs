using System.Net;
using System.Net.Sockets;

namespace Dockyard.Agent;

static class InterfaceAddress
{
    public static IPAddress? Of(string name)
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(i => i.Name == name)?.GetIPProperties().UnicastAddresses
                .Select(a => a.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return null;
        }
    }
}
