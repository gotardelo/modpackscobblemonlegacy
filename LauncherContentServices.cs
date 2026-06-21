using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CobblemonLegacy;

internal static class LauncherUpdateService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/gotardelo/cobblemonlegacy-downloads/releases/latest";
    private const string InstallerAssetName = "CobblemonLegacyLauncherSetup.exe";

    public static async Task<LauncherUpdateInfo?> CheckForUpdateAsync(HttpClient http, string currentVersion)
    {
        using var stream = await http.GetStreamAsync(LatestReleaseApiUrl);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, LauncherRuntime.JsonOptions);
        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            return null;

        var releaseVersion = ParseVersion(release.TagName);
        var localVersion = ParseVersion(currentVersion);
        if (releaseVersion <= localVersion)
            return null;

        var installer = release.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, InstallerAssetName, StringComparison.OrdinalIgnoreCase));
        if (installer is null || string.IsNullOrWhiteSpace(installer.BrowserDownloadUrl))
            return null;

        return new LauncherUpdateInfo(
            releaseVersion,
            release.TagName,
            installer.BrowserDownloadUrl,
            release.HtmlUrl,
            release.Name,
            release.Body,
            installer.Size,
            installer.Digest);
    }

    public static async Task<string> DownloadInstallerAsync(
        HttpClient http,
        LauncherUpdateInfo update,
        Action<long, long>? byteProgress = null)
    {
        var updatesDir = Path.Combine(
            Path.GetDirectoryName(LauncherSettings.SettingsPath)!,
            "updates");
        Directory.CreateDirectory(updatesDir);

        var targetPath = Path.Combine(updatesDir, $"CobblemonLegacyLauncherSetup-{update.TagName}.exe");
        var tempPath = $"{targetPath}.download";

        try
        {
            using var response = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var destination = File.Create(tempPath))
            {
                await CopyWithProgressAsync(source, destination, response.Content.Headers.ContentLength ?? update.Size, byteProgress);
            }

            await VerifyDigestIfAvailableAsync(tempPath, update.Digest);
            File.Move(tempPath, targetPath, true);
            return targetPath;
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    public static void StartInstallerAndExit(string installerPath)
    {
        Process.Start(new ProcessStartInfo(installerPath, "/CURRENTUSER /SUPPRESSMSGBOXES /NORESTART")
        {
            UseShellExecute = true
        });
    }

    private static Version ParseVersion(string value)
    {
        var cleaned = value
            .Replace("launcher-v", "", StringComparison.OrdinalIgnoreCase)
            .Replace("v", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        return Version.TryParse(cleaned, out var version) ? version : new Version(0, 0, 0);
    }

    private static async Task CopyWithProgressAsync(Stream source, Stream destination, long? totalBytes, Action<long, long>? byteProgress)
    {
        var buffer = new byte[1024 * 128];
        long copied = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer);
            if (read == 0)
                break;

            await destination.WriteAsync(buffer.AsMemory(0, read));
            copied += read;

            if (totalBytes is > 0)
                byteProgress?.Invoke(copied, totalBytes.Value);
        }
    }

    private static async Task VerifyDigestIfAvailableAsync(string path, string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            return;

        var expected = digest["sha256:".Length..].Trim();
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Hash do instalador baixado nao confere com a release do GitHub.");
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("body")]
        public string Body { get; set; } = "";

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = "";

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("digest")]
        public string Digest { get; set; } = "";
    }
}

internal sealed record LauncherUpdateInfo(
    Version Version,
    string TagName,
    string DownloadUrl,
    string ReleaseUrl,
    string Name,
    string Body,
    long Size,
    string? Digest);

internal static class LauncherNewsService
{
    public static string CachePath { get; } = Path.Combine(
        Path.GetDirectoryName(LauncherSettings.SettingsPath)!,
        "news-cache.json");

    public static async Task<LauncherNewsItem?> LoadLatestAsync(HttpClient http, JsonSerializerOptions jsonOptions)
    {
        try
        {
            using var stream = await http.GetStreamAsync(ProgramDefaults.NewsUrl);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var json = await reader.ReadToEndAsync();
            await SaveCacheAsync(json);
            return ParseLatest(json, jsonOptions);
        }
        catch
        {
            if (!File.Exists(CachePath))
                return null;

            var cachedJson = await File.ReadAllTextAsync(CachePath, Encoding.UTF8);
            return ParseLatest(cachedJson, jsonOptions);
        }
    }

    private static LauncherNewsItem? ParseLatest(string json, JsonSerializerOptions jsonOptions)
    {
        var feed = JsonSerializer.Deserialize<LauncherNewsFeed>(json, jsonOptions);
        return feed?.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.Title) || !string.IsNullOrWhiteSpace(item.Message))
            .OrderByDescending(item => item.PublishedAt)
            .FirstOrDefault();
    }

    private static async Task SaveCacheAsync(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            await File.WriteAllTextAsync(CachePath, json, Encoding.UTF8);
        }
        catch
        {
            // News cache is optional.
        }
    }
}

internal sealed class LauncherNewsFeed
{
    public List<LauncherNewsItem> Items { get; set; } = [];
}

internal sealed class LauncherNewsItem
{
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.MinValue;
}
