namespace Dockyard.Agent;

readonly record struct ForwardHeader(ForwardProtocol Protocol, int Port)
{
    public override string ToString() => $"{(Protocol == ForwardProtocol.Tcp ? "tcp" : "udp")} {Port}";

    public static ForwardHeader? Parse(string line)
    {
        var parts = line.Trim().Split(' ');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port) || port is < 1 or > 65535) return null;
        return parts[0] switch
        {
            "tcp" => new ForwardHeader(ForwardProtocol.Tcp, port),
            "udp" => new ForwardHeader(ForwardProtocol.Udp, port),
            _ => null,
        };
    }
}
