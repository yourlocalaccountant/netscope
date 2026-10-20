using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
namespace NetScope;
sealed record RemoteName(string Hostname, string Status);
sealed class RemoteNames
{
    readonly ConcurrentDictionary<string, RemoteName> cache = new();
    public RemoteName Get(IPAddress ip) => cache.GetValueOrDefault(ip.ToString()) ?? new("Unresolved", "Not queried");
    public async Task Resolve(IPAddress ip)
    {
        string key = ip.ToString();
        if (!cache.TryAdd(key, new("Resolving…", "Resolving…"))) return;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) { cache[key] = new("—", "No remote endpoint"); return; }
        try
        {
            var entry = await Dns.GetHostEntryAsync(ip).WaitAsync(TimeSpan.FromSeconds(5));
            bool found = !IPAddress.TryParse(entry.HostName, out _);
            cache[key] = found ? new(entry.HostName, "Resolved (PTR)") : new("Unresolved", "No PTR record");
        }
        catch { cache[key] = new("Unresolved", "No PTR record / lookup failed"); }
    }
    public static async Task<string> Whois(IPAddress ip)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NetScope/0.2");
        using var response = await client.GetAsync("https://rdap.org/ip/" + Uri.EscapeDataString(ip.ToString()));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }
}
