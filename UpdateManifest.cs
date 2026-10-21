using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetScope;

sealed record UpdateManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("installer")] string Installer,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("bytes")] long Bytes)
{
    public const string InstallerName = "NetScope-Setup-x64.exe";

    public static UpdateManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json) ?? throw new FormatException("The update manifest is empty.");
        if (!System.Version.TryParse(manifest.Version, out _))
            throw new FormatException("The update version is invalid.");
        if (manifest.Installer != InstallerName || manifest.Bytes is < 1024 or > 100_000_000)
            throw new FormatException("The update installer metadata is invalid.");
        if (manifest.Sha256 == null || manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit))
            throw new FormatException("The update checksum is invalid.");
        return manifest;
    }

    public bool IsNewerThan(Version installed) => System.Version.Parse(Version) > installed;
    public bool MatchesHash(string actual) => Sha256.Equals(actual, StringComparison.OrdinalIgnoreCase);
}
