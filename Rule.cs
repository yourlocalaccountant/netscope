namespace NetScope;
enum RuleKind { Block, UploadLimit, Priority, Quota }
sealed class Rule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public RuleKind Kind { get; set; }
    public string Direction { get; set; } = "Both";
    public long Value { get; set; }
    public bool Enabled { get; set; } = true;
    public bool QuotaBlock { get; set; } = true;
    public bool QuotaTriggered { get; set; }
    public string RemoteAddress { get; set; } = "";
    public int RemotePort { get; set; }
    public string LocalAddress { get; set; } = "";
    public int LocalPort { get; set; }
    public string Protocol { get; set; } = "TCP";
    public bool IsConnectionScoped => RemoteAddress.Length > 0;
    public string ScopeKey => IsConnectionScoped ? $"{Protocol}|{LocalAddress}|{LocalPort}|{RemoteAddress}|{RemotePort}" : "";
    public bool AppliesTo(FlowId flow) => !IsConnectionScoped || ScopeKey == flow.Scope;
    public string Application => System.IO.Path.GetFileName(Path);
    public string Description => Kind switch { RuleKind.Block => $"Block {Direction.ToLowerInvariant()}", RuleKind.UploadLimit => $"Upload ≤ {Value / 1000.0:F0} Kbps", RuleKind.Priority => $"DSCP {Value}", _ => $"{(QuotaTriggered ? "Reached / blocked" : QuotaBlock ? "Block at" : "Alert at")} {Display.Bytes(Value)} / session" };
}
