using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetScope;

sealed class GitHubUpdater : IDisposable
{
    const string Repository = "yourlocalaccountant/netscope";
    readonly HttpClient client;
    readonly string folder;
    readonly bool ownsClient;

    public GitHubUpdater(HttpClient? client = null, string? folder = null)
    {
        ownsClient = client == null;
        this.client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        this.folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetScope", "updates");
    }

    static HttpRequestMessage Request(string path, string token, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + Repository + "/contents/downloads/" + path + "?ref=main");
        request.Headers.UserAgent.ParseAdd("NetScope-Updater/0.5");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    public async Task<UpdateManifest> CheckAsync(string token, CancellationToken cancellationToken = default)
    {
        using var request = Request("update.json", token, "application/vnd.github+json");
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw AccessError(response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        string encoded = document.RootElement.GetProperty("content").GetString() ?? "";
        string json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        return UpdateManifest.Parse(json);
    }

    public async Task<string> DownloadAsync(UpdateManifest manifest, string token, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        string destination = Path.Combine(folder, $"NetScope-Setup-{System.Version.Parse(manifest.Version)}.exe");
        if (File.Exists(destination) && manifest.MatchesHash(await HashFile(destination, cancellationToken))) return destination;

        string temporary = destination + ".part";
        try
        {
            using var request = Request(UpdateManifest.InstallerName, token, "application/vnd.github.raw");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) throw AccessError(response.StatusCode);
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidOperationException("GitHub returned metadata instead of the installer. Check that the token can read repository contents.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[131072]; long total = 0, lastReport = 0;
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
            {
                while (true)
                {
                    int read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    total += read;
                    if (total > manifest.Bytes) throw new InvalidDataException("The downloaded installer is larger than expected.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    if (total - lastReport >= 1_048_576) { progress?.Report(total); lastReport = total; }
                }
                await output.FlushAsync(cancellationToken);
            }
            if (total != manifest.Bytes || !manifest.MatchesHash(Convert.ToHexString(hash.GetHashAndReset())))
                throw new InvalidDataException("The installer failed its size or SHA-256 check. No update was started.");
            File.Move(temporary, destination, true);
            progress?.Report(total);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    static async Task<string> HashFile(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    static Exception AccessError(System.Net.HttpStatusCode status) => status switch
    {
        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound =>
            new InvalidOperationException("GitHub could not open the private update. Use a token with read access to this repository's Contents."),
        _ => new HttpRequestException($"GitHub returned HTTP {(int)status} while checking for updates.")
    };

    public void Dispose() { if (ownsClient) client.Dispose(); }
}
