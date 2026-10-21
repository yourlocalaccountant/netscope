namespace NetScope;
sealed record AppInfo(int Pid, string Name, string Path, DateTime Started);
sealed record ApplicationGroup(string Id, AppInfo Representative, IReadOnlyList<AppInfo> Processes)
{
    public static string Identity(AppInfo app) => app.Path.Length > 0 ? "path:" + app.Path.ToUpperInvariant() : "pid:" + app.Pid;
    public static ApplicationGroup[] Create(IEnumerable<AppInfo> apps) => apps.GroupBy(Identity).Select(g => new ApplicationGroup(g.Key, g.First(), g.ToArray())).ToArray();
    public bool Contains(int pid) => Processes.Any(p => p.Pid == pid);
}
