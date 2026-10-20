using System.Net;
namespace NetScope;
static class IpClassifier
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (IPAddress.IsLoopback(address)) return false;
        if (b.Length == 16) return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 1 && b[2] == 0x0d && b[3] == 0xb8);
        return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && (b[1] == 168 || (b[1] == 0 && b[2] is 0 or 2))) || (b[0] == 198 && b[1] == 51 && b[2] == 100) || (b[0] == 203 && b[1] == 0 && b[2] == 113) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || (b[0] == 198 && b[1] is 18 or 19));
    }
}
