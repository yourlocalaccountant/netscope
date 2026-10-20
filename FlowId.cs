using System.Net;
namespace NetScope;
readonly record struct FlowId(int Pid, string Protocol, string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort)
{
    public static string Address(IPAddress address) => (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : new IPAddress(address.GetAddressBytes())).ToString();
    public static FlowId Create(int pid, string protocol, IPAddress local, int localPort, IPAddress remote, int remotePort) => new(pid, protocol.ToUpperInvariant(), Address(local), localPort, Address(remote), remotePort);
    public string Scope => $"{Protocol}|{LocalAddress}|{LocalPort}|{RemoteAddress}|{RemotePort}";
}
