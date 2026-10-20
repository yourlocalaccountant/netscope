namespace NetScope;
readonly record struct TrafficPoint(DateTime Time, double Down, double Up);
sealed class TrafficTimeline
{
    readonly Dictionary<string, Queue<TrafficPoint>> series = new();
    readonly int maximumSeries;
    public TrafficTimeline(int maximumSeries = 1024) => this.maximumSeries = maximumSeries;
    public void Record(string key, DateTime time, double down, double up)
    {
        bool idle = down == 0 && up == 0;
        if (!series.TryGetValue(key, out var points))
        {
            if (idle) return;
            if (series.Count >= maximumSeries) series.Remove(series.MinBy(x => x.Value.Last().Time).Key);
            series[key] = points = new();
        }
        while (points.Count > 0 && points.Peek().Time < time.AddMinutes(-10)) points.Dequeue();
        if (!idle || points.Count == 0 || points.Last().Down != 0 || points.Last().Up != 0) points.Enqueue(new(time, down, up));
        while (points.Count > 600) points.Dequeue();
    }
    public TrafficPoint[] Get(string key, DateTime now)
    {
        if (!series.TryGetValue(key, out var points)) return Array.Empty<TrafficPoint>();
        var result = points.Where(p => p.Time >= now.AddMinutes(-10)).ToList();
        if (result.Count > 0 && result[^1].Time < now) result.Add(new(now, 0, 0));
        return result.ToArray();
    }
}
