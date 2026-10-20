namespace NetScope;
static class Display
{
    public static bool Bits { get; set; }
    public static string Bytes(double n) => n >= 1073741824 ? $"{n / 1073741824:F2} GB" : n >= 1048576 ? $"{n / 1048576:F2} MB" : n >= 1024 ? $"{n / 1024:F1} KB" : $"{n:F0} B";
    public static string Rate(double n) => Bits ? (n * 8 >= 1000000 ? $"{n * 8 / 1000000:F2} Mbps" : $"{n * 8 / 1000:F1} Kbps") : Bytes(n) + "/s";
}
