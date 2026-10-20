namespace NetScope;
static class DisplaySchedule
{
    public static bool Due(DateTime last, DateTime now, int seconds, bool paused) => !paused && (last == DateTime.MinValue || (seconds > 0 && now - last >= TimeSpan.FromSeconds(seconds)));
}
