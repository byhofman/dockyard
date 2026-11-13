namespace Dockyard.Agent;

readonly record struct DnsQuestion(string Name, ushort Type, int End)
{
    public const ushort A = 1;
}
