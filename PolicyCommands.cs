using System.Security.Cryptography;
using System.Text;
using System.Net;
namespace NetScope;
static class PolicyCommands
{
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    public static string QosName(string path, string scope = "") => "NetScope-QoS-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant() + (scope.Length == 0 ? "" : "|" + scope))))[..16];
    public static string Firewall(Rule rule, bool enabled)
    {
        var s = new StringBuilder();
        foreach (string direction in new[] { "Inbound", "Outbound" })
        {
            string name = "NetScope-" + rule.Id + "-" + direction;
            s.AppendLine($"Get-NetFirewallRule -Name {Quote(name)} -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction Stop");
            if (enabled && (rule.Direction == "Both" || rule.Direction == direction))
                s.AppendLine($"New-NetFirewallRule -Name {Quote(name)} -DisplayName {Quote("NetScope: " + rule.Application + " " + direction)} -Group 'NetScope' -Program {Quote(rule.Path)} -Direction {direction} -Action Block -Profile Any -Enabled True{FirewallScope(rule)} -ErrorAction Stop | Out-Null");
        }
        return s.ToString();
    }
    static string FirewallScope(Rule rule)
    {
        if (!rule.IsConnectionScoped) return "";
        string protocol = rule.Protocol == "UDP" ? "UDP" : "TCP";
        return $" -Protocol {protocol} -LocalAddress {Quote(rule.LocalAddress)} -LocalPort {rule.LocalPort} -RemoteAddress {Quote(rule.RemoteAddress)} -RemotePort {rule.RemotePort}";
    }
    public static string Qos(string path, IEnumerable<Rule> rules, Rule? scope = null)
    {
        string key = scope?.ScopeKey ?? "";
        string name = QosName(path, key);
        var all = rules.Where(r => r.Enabled && r.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
        var active = all.Where(r => r.ScopeKey == key).ToArray();
        var parent = all.Where(r => !r.IsConnectionScoped).ToArray();
        bool hasOwnPolicy = active.Any(r => r.Kind is RuleKind.UploadLimit or RuleKind.Priority);
        var limit = active.FirstOrDefault(r => r.Kind == RuleKind.UploadLimit) ?? (key.Length == 0 ? null : parent.FirstOrDefault(r => r.Kind == RuleKind.UploadLimit));
        var priority = active.FirstOrDefault(r => r.Kind == RuleKind.Priority) ?? (key.Length == 0 ? null : parent.FirstOrDefault(r => r.Kind == RuleKind.Priority));
        string script = $"Get-NetQosPolicy -Name {Quote(name)} -ErrorAction SilentlyContinue | Remove-NetQosPolicy -Confirm:$false -ErrorAction Stop\n";
        if (hasOwnPolicy)
        {
            string conditions = "";
            if (scope?.IsConnectionScoped == true)
            {
                string Prefix(string ip) => ip + (IPAddress.Parse(ip).AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "/128" : "/32");
                conditions = $" -IPProtocolMatchCondition {(scope.Protocol == "UDP" ? "UDP" : "TCP")} -IPSrcPrefixMatchCondition {Quote(Prefix(scope.LocalAddress))} -IPDstPrefixMatchCondition {Quote(Prefix(scope.RemoteAddress))} -IPSrcPortMatchCondition {scope.LocalPort} -IPDstPortMatchCondition {scope.RemotePort}";
            }
            script += $"New-NetQosPolicy -Name {Quote(name)} -AppPathNameMatchCondition {Quote(path)} -NetworkProfile All -Precedence {(key.Length == 0 ? 100 : 200)}{conditions}" + (limit == null ? "" : $" -ThrottleRateActionBitsPerSecond {limit.Value}") + (priority == null ? "" : $" -DSCPAction {priority.Value}") + " -ErrorAction Stop | Out-Null\n";
        }
        return script;
    }
    public static string AffectedQos(Rule changed, IEnumerable<Rule> target, IEnumerable<Rule> bothStates)
    {
        var scopes = bothStates.Where(r => r.Path.Equals(changed.Path, StringComparison.OrdinalIgnoreCase) && (r.Kind is RuleKind.UploadLimit or RuleKind.Priority)).GroupBy(r => r.ScopeKey).Select(g => g.First()).ToArray();
        if (changed.IsConnectionScoped) return Qos(changed.Path, target, changed);
        return Qos(changed.Path, target) + string.Concat(scopes.Where(r => r.IsConnectionScoped).Select(r => Qos(changed.Path, target, r)));
    }
}
