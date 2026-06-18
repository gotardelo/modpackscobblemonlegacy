using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;

namespace CobblemonLegacy;

internal static class LauncherRuntime
{
    public const string LauncherName = "Cobblemon Legacy";
    public const string ServerIp = "enx-cirion-16.enx.host:10068";
    public const string ServerHost = "Enxada Host";
    private const int StaleGameProcessSeconds = 30;
    private static readonly Regex SensitiveLaunchArgumentRegex = new(
        @"(--(?:accessToken|uuid|xuid|clientId)\s+)(""[^""]*""|\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };

        var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CobblemonLegacyLauncher", "1.0"));
        return http;
    }

    public static MinecraftLauncher CreateMinecraftLauncher(
        string gameDir,
        Action<string>? log = null,
        Action<long, long>? byteProgress = null)
    {
        var launcher = new MinecraftLauncher(new MinecraftPath(gameDir));
        var lastFileProgressLog = DateTime.MinValue;
        launcher.FileProgressChanged += (_, args) =>
        {
            var now = DateTime.Now;
            if ((now - lastFileProgressLog).TotalMilliseconds < 750 && args.ProgressedTasks != args.TotalTasks)
                return;

            lastFileProgressLog = now;
            if (args.TotalTasks <= 0)
            {
                log?.Invoke($"Minecraft: {args.Name}");
                return;
            }

            log?.Invoke($"Minecraft: {args.ProgressedTasks}/{args.TotalTasks} - {args.Name}");
        };
        launcher.ByteProgressChanged += (_, args) =>
        {
            if (args.TotalBytes > 0)
                byteProgress?.Invoke(args.ProgressedBytes, args.TotalBytes);
        };
        return launcher;
    }

    public static async Task<string> InstallOrUpdateAsync(
        HttpClient http,
        MinecraftLauncher launcher,
        ModpackManifest manifest,
        string gameDir,
        Action<string>? log = null,
        Action<long, long>? byteProgress = null)
    {
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(Path.Combine(gameDir, "mods"));
        Directory.CreateDirectory(Path.Combine(gameDir, "resourcepacks"));
        Directory.CreateDirectory(Path.Combine(gameDir, "config"));

        if (IsVersionInstalled(gameDir, manifest.MinecraftVersion))
        {
            log?.Invoke($"Minecraft {manifest.MinecraftVersion} ja instalado.");
        }
        else
        {
            log?.Invoke($"Instalando Minecraft {manifest.MinecraftVersion}...");
            await launcher.InstallAsync(manifest.MinecraftVersion);
        }

        log?.Invoke("Preparando Fabric...");
        var fabricVersionId = await FabricProfileInstaller.InstallAsync(http, gameDir, manifest.MinecraftVersion, manifest.FabricLoaderVersion, log);

        if (IsVersionInstalled(gameDir, fabricVersionId))
        {
            log?.Invoke($"Fabric {fabricVersionId} ja instalado.");
        }
        else
        {
            log?.Invoke($"Instalando bibliotecas da versao {fabricVersionId}...");
            await launcher.InstallAsync(fabricVersionId);
        }

        log?.Invoke("Sincronizando mods, resourcepacks e configs...");
        await ManagedFileSynchronizer.SyncAsync(http, gameDir, manifest, JsonOptions, log, byteProgress);

        return fabricVersionId;
    }

    private static bool IsVersionInstalled(string gameDir, string versionId)
    {
        return File.Exists(Path.Combine(gameDir, "versions", versionId, $"{versionId}.json"));
    }

    public static async Task<System.Diagnostics.Process> StartGameAsync(
        MinecraftLauncher launcher,
        string versionId,
        LauncherSettings settings,
        MSession session,
        Action<string>? log = null)
    {
        var gameDir = ExpandGameDirectory(settings);
        var stoppedProcesses = StopStaleGameProcesses(gameDir, log);
        if (stoppedProcesses > 0)
            await Task.Delay(750);

        var process = await launcher.BuildProcessAsync(versionId, new MLaunchOption
        {
            Session = session,
            MaximumRamMb = settings.MaximumRamMb,
            MinimumRamMb = Math.Min(1024, settings.MaximumRamMb),
            GameLauncherName = "CobblemonLegacy",
            GameLauncherVersion = "1.0"
        });

        ConfigureMinecraftProcess(process, log);
        await WriteLaunchDiagnosticsAsync(process.StartInfo);

        if (!process.Start())
            throw new InvalidOperationException("O processo do Minecraft nao iniciou.");

        BeginMinecraftOutputRead(process, log);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            try
            {
                var message = process.ExitCode == 0
                    ? "Minecraft fechado."
                    : $"Minecraft fechou com codigo {process.ExitCode}. Confira o latest.log.";
                log?.Invoke(message);
            }
            catch
            {
                // Ignore diagnostics failures after the game process exits.
            }
        };

        return process;
    }

    private static void ConfigureMinecraftProcess(System.Diagnostics.Process process, Action<string>? log)
    {
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = false;
        process.StartInfo.WindowStyle = ProcessWindowStyle.Normal;
        process.StartInfo.RedirectStandardOutput = false;
        process.StartInfo.RedirectStandardError = false;

        log?.Invoke($"Java: {Path.GetFileName(process.StartInfo.FileName)}");
    }

    private static void BeginMinecraftOutputRead(System.Diagnostics.Process process, Action<string>? log)
    {
        if (!process.StartInfo.RedirectStandardOutput && !process.StartInfo.RedirectStandardError)
            return;

        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
                log?.Invoke($"Minecraft: {args.Data}");
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
                log?.Invoke($"Minecraft: {args.Data}");
        };

        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (InvalidOperationException)
        {
            log?.Invoke("Nao foi possivel acompanhar a saida do Minecraft.");
        }
    }

    private static int StopStaleGameProcesses(string gameDir, Action<string>? log)
    {
        var stopped = 0;
        var runtimeDir = Path.Combine(Path.GetFullPath(gameDir), "runtime");

        foreach (var processName in new[] { "java", "javaw" })
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        var executable = process.MainModule?.FileName;
                        if (string.IsNullOrWhiteSpace(executable) || !IsInsideDirectory(executable, runtimeDir))
                            continue;

                        if (process.MainWindowHandle != IntPtr.Zero)
                            continue;

                        var age = DateTime.Now - process.StartTime;
                        if (age.TotalSeconds < StaleGameProcessSeconds)
                            continue;

                        log?.Invoke($"Fechando Minecraft antigo sem janela (PID {process.Id}).");
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                        stopped++;
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"Nao foi possivel verificar processo {process.Id}: {ex.Message}");
                    }
                }
            }
        }

        return stopped;
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory);
        var directoryWithSeparator = fullDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? fullDirectory
            : $"{fullDirectory}{Path.DirectorySeparatorChar}";

        return fullPath.StartsWith(directoryWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteLaunchDiagnosticsAsync(ProcessStartInfo startInfo)
    {
        var diagnosticsDir = Path.GetDirectoryName(LauncherSettings.SettingsPath)!;
        Directory.CreateDirectory(diagnosticsDir);

        var logPath = Path.Combine(diagnosticsDir, "launcher.log");
        var commandPath = Path.Combine(diagnosticsDir, "last-launch-command.txt");
        var sanitizedArguments = SanitizeLaunchArguments(startInfo.Arguments);

        var entry = new StringBuilder()
            .AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Starting Minecraft")
            .AppendLine($"FileName: {startInfo.FileName}")
            .AppendLine($"WorkingDirectory: {startInfo.WorkingDirectory}")
            .AppendLine($"ArgumentsLength: {startInfo.Arguments.Length}")
            .AppendLine();

        await File.AppendAllTextAsync(logPath, entry.ToString(), Encoding.UTF8);
        await File.WriteAllTextAsync(commandPath, $"{startInfo.FileName} {sanitizedArguments}", Encoding.UTF8);
    }

    private static string SanitizeLaunchArguments(string arguments)
    {
        return SensitiveLaunchArgumentRegex.Replace(arguments, "$1<hidden>");
    }

    public static string ExpandGameDirectory(LauncherSettings settings)
    {
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.GameDirectory));
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }
}

internal static class AuthModes
{
    public const string Microsoft = "microsoft";
    public const string Offline = "offline";
}

internal sealed class LauncherSettings
{
    private const int RecommendedRamMb = 8192;

    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CobblemonLegacyLauncher",
        "launcher.settings.json");

    public string ManifestUrl { get; set; } = ProgramDefaults.ManifestUrl;
    public string GameDirectory { get; set; } = "%APPDATA%\\.cobblemonlegacy";
    public string OfflineUsername { get; set; } = "Player";
    public string AuthMode { get; set; } = "";
    public string MicrosoftUsername { get; set; } = "";
    public int MaximumRamMb { get; set; } = RecommendedRamMb;
    public int PerformancePresetVersion { get; set; }

    public static async Task<LauncherSettings> LoadAsync(JsonSerializerOptions jsonOptions)
    {
        if (!File.Exists(SettingsPath))
        {
            var settings = new LauncherSettings();
            await settings.SaveAsync(jsonOptions);
            return settings;
        }

        var json = await File.ReadAllTextAsync(SettingsPath, Encoding.UTF8);
        var loaded = JsonSerializer.Deserialize<LauncherSettings>(json, jsonOptions) ?? new LauncherSettings();
        var normalized = false;

        if (loaded.MaximumRamMb < RecommendedRamMb)
        {
            loaded.MaximumRamMb = RecommendedRamMb;
            normalized = true;
        }

        loaded.OfflineUsername = string.IsNullOrWhiteSpace(loaded.OfflineUsername) ? "Player" : loaded.OfflineUsername.Trim();
        loaded.ManifestUrl = string.IsNullOrWhiteSpace(loaded.ManifestUrl) ? ProgramDefaults.ManifestUrl : loaded.ManifestUrl.Trim();
        loaded.GameDirectory = string.IsNullOrWhiteSpace(loaded.GameDirectory) ? "%APPDATA%\\.cobblemonlegacy" : loaded.GameDirectory.Trim();
        loaded.AuthMode = NormalizeAuthMode(loaded.AuthMode);
        loaded.MicrosoftUsername = loaded.MicrosoftUsername?.Trim() ?? "";

        if (normalized)
            await loaded.SaveAsync(jsonOptions);

        return loaded;
    }

    public async Task SaveAsync(JsonSerializerOptions jsonOptions)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        AuthMode = NormalizeAuthMode(AuthMode);
        OfflineUsername = string.IsNullOrWhiteSpace(OfflineUsername) ? "Player" : OfflineUsername.Trim();
        ManifestUrl = string.IsNullOrWhiteSpace(ManifestUrl) ? ProgramDefaults.ManifestUrl : ManifestUrl.Trim();
        GameDirectory = string.IsNullOrWhiteSpace(GameDirectory) ? "%APPDATA%\\.cobblemonlegacy" : GameDirectory.Trim();
        MaximumRamMb = Math.Max(1024, MaximumRamMb);

        await File.WriteAllTextAsync(SettingsPath, JsonSerializer.Serialize(this, jsonOptions), Encoding.UTF8);
    }

    private static string NormalizeAuthMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            AuthModes.Microsoft => AuthModes.Microsoft,
            AuthModes.Offline => AuthModes.Offline,
            _ => ""
        };
    }
}

internal static class ProgramDefaults
{
    public const string ManifestUrl = "https://raw.githubusercontent.com/gotardelo/modpackscobblemonlegacy/main/manifest.json";
    public const string FabricMetaBaseUrl = "https://meta.fabricmc.net/v2";
}

internal sealed class ModpackManifest
{
    public string Name { get; set; } = "Cobblemon Legacy";
    public string Version { get; set; } = "1.0.0";
    public string MinecraftVersion { get; set; } = "1.21.1";
    public string FabricLoaderVersion { get; set; } = "latest";
    public List<ManagedFile> Files { get; set; } = [];
}

internal sealed class ManagedFile
{
    public string Path { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long? Size { get; set; }
    public bool Required { get; set; } = true;
}

internal static class ModpackManifestLoader
{
    public static string CachePath { get; } = Path.Combine(
        Path.GetDirectoryName(LauncherSettings.SettingsPath)!,
        "manifest-cache.json");

    public static async Task<ModpackManifest?> TryLoadCachedAsync(JsonSerializerOptions jsonOptions)
    {
        try
        {
            if (!File.Exists(CachePath))
                return null;

            var json = await File.ReadAllTextAsync(CachePath, Encoding.UTF8);
            return Normalize(Deserialize(json, jsonOptions));
        }
        catch
        {
            return null;
        }
    }

    public static async Task<ModpackManifest> LoadAsync(
        HttpClient http,
        string manifestLocation,
        JsonSerializerOptions jsonOptions,
        Action<string>? log = null)
    {
        var localFallbacks = new[]
        {
            Path.Combine(Environment.CurrentDirectory, "manifest.json"),
            Path.Combine(AppContext.BaseDirectory, "manifest.json")
        };

        if (IsHttpUrl(manifestLocation))
        {
            try
            {
                log?.Invoke("Baixando manifest do modpack...");
                var json = await http.GetStringAsync(manifestLocation);
                await SaveCacheAsync(json);
                return Normalize(Deserialize(json, jsonOptions));
            }
            catch (Exception ex)
            {
                log?.Invoke($"Nao foi possivel baixar o manifest remoto: {ex.Message}");
                log?.Invoke("Tentando manifest local...");
            }
        }
        else if (!string.IsNullOrWhiteSpace(manifestLocation))
        {
            var localPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(manifestLocation));
            if (File.Exists(localPath))
                return Normalize(Deserialize(await File.ReadAllTextAsync(localPath, Encoding.UTF8), jsonOptions));
        }

        foreach (var fallback in localFallbacks)
        {
            if (File.Exists(fallback))
            {
                log?.Invoke($"Usando manifest local: {fallback}");
                var json = await File.ReadAllTextAsync(fallback, Encoding.UTF8);
                await SaveCacheAsync(json);
                return Normalize(Deserialize(json, jsonOptions));
            }
        }

        throw new InvalidOperationException("Nenhum manifest encontrado. Ajuste launcher.settings.json ou coloque manifest.json ao lado do executavel.");
    }

    private static ModpackManifest Deserialize(string json, JsonSerializerOptions jsonOptions)
    {
        return JsonSerializer.Deserialize<ModpackManifest>(json, jsonOptions)
               ?? throw new InvalidOperationException("Manifest vazio ou invalido.");
    }

    private static ModpackManifest Normalize(ModpackManifest manifest)
    {
        manifest.Name = string.IsNullOrWhiteSpace(manifest.Name) ? "Cobblemon Legacy" : manifest.Name.Trim();
        manifest.Version = string.IsNullOrWhiteSpace(manifest.Version) ? "1.0.0" : manifest.Version.Trim();
        manifest.MinecraftVersion = string.IsNullOrWhiteSpace(manifest.MinecraftVersion) ? "1.21.1" : manifest.MinecraftVersion.Trim();
        manifest.FabricLoaderVersion = string.IsNullOrWhiteSpace(manifest.FabricLoaderVersion) ? "latest" : manifest.FabricLoaderVersion.Trim();
        manifest.Files ??= [];
        return manifest;
    }

    private static bool IsHttpUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
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
            // Cache is an optimization only.
        }
    }
}

internal static class FabricProfileInstaller
{
    public static async Task<string> InstallAsync(
        HttpClient http,
        string gameDir,
        string minecraftVersion,
        string loaderVersion,
        Action<string>? log = null)
    {
        var resolvedLoaderVersion = await ResolveLoaderVersionAsync(http, gameDir, minecraftVersion, loaderVersion, log);
        var expectedVersionId = $"fabric-loader-{resolvedLoaderVersion}-{minecraftVersion}";
        var expectedJsonPath = Path.Combine(gameDir, "versions", expectedVersionId, $"{expectedVersionId}.json");

        try
        {
            var profileUrl = $"{ProgramDefaults.FabricMetaBaseUrl}/versions/loader/{Uri.EscapeDataString(minecraftVersion)}/{Uri.EscapeDataString(resolvedLoaderVersion)}/profile/json";
            var json = await http.GetStringAsync(profileUrl);
            using var document = JsonDocument.Parse(json);
            var versionId = document.RootElement.GetProperty("id").GetString() ?? expectedVersionId;
            var versionDir = Path.Combine(gameDir, "versions", versionId);

            Directory.CreateDirectory(versionDir);
            await File.WriteAllTextAsync(Path.Combine(versionDir, $"{versionId}.json"), json, Encoding.UTF8);
            log?.Invoke($"Fabric {resolvedLoaderVersion} instalado.");
            return versionId;
        }
        catch (Exception ex) when (File.Exists(expectedJsonPath))
        {
            log?.Invoke($"Nao foi possivel atualizar o perfil Fabric ({ex.Message}). Usando perfil local.");
            return expectedVersionId;
        }
    }

    private static async Task<string> ResolveLoaderVersionAsync(
        HttpClient http,
        string gameDir,
        string minecraftVersion,
        string loaderVersion,
        Action<string>? log = null)
    {
        if (!string.Equals(loaderVersion, "latest", StringComparison.OrdinalIgnoreCase))
            return loaderVersion;

        try
        {
            var url = $"{ProgramDefaults.FabricMetaBaseUrl}/versions/loader/{Uri.EscapeDataString(minecraftVersion)}";
            using var stream = await http.GetStreamAsync(url);
            using var document = await JsonDocument.ParseAsync(stream);

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var loader = entry.GetProperty("loader");
                if (loader.TryGetProperty("stable", out var stable) && stable.ValueKind == JsonValueKind.True)
                    return loader.GetProperty("version").GetString()!;
            }

            var first = document.RootElement.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
                return first.GetProperty("loader").GetProperty("version").GetString()!;
        }
        catch (Exception ex)
        {
            var installed = FindInstalledFabricVersion(gameDir, minecraftVersion);
            if (installed is not null)
            {
                log?.Invoke($"Nao foi possivel consultar Fabric latest ({ex.Message}). Usando {installed.LoaderVersion} local.");
                return installed.LoaderVersion;
            }

            throw;
        }

        throw new InvalidOperationException($"Nenhum Fabric Loader encontrado para Minecraft {minecraftVersion}.");
    }

    private static InstalledFabricVersion? FindInstalledFabricVersion(string gameDir, string minecraftVersion)
    {
        var versionsDir = Path.Combine(gameDir, "versions");
        if (!Directory.Exists(versionsDir))
            return null;

        var suffix = $"-{minecraftVersion}";
        foreach (var dir in Directory.EnumerateDirectories(versionsDir, $"fabric-loader-*{suffix}"))
        {
            var id = Path.GetFileName(dir);
            var loaderVersion = id["fabric-loader-".Length..^suffix.Length];
            if (!string.IsNullOrWhiteSpace(loaderVersion))
                return new InstalledFabricVersion(id, loaderVersion);
        }

        return null;
    }

    private sealed record InstalledFabricVersion(string VersionId, string LoaderVersion);
}

internal static class ManagedFileSynchronizer
{
    private const int MaxParallelDownloads = 4;
    private const string StateFileName = ".cobblemonlegacy-launcher-state.json";

    public static async Task SyncAsync(
        HttpClient http,
        string gameDir,
        ModpackManifest manifest,
        JsonSerializerOptions jsonOptions,
        Action<string>? log = null,
        Action<long, long>? byteProgress = null)
    {
        var statePath = Path.Combine(gameDir, StateFileName);
        var state = await LoadStateAsync(statePath, jsonOptions);
        var previousManagedFiles = new HashSet<string>(state.ManagedFiles, StringComparer.OrdinalIgnoreCase);
        var canTrustInstalledFiles = string.Equals(state.ManifestVersion, manifest.Version, StringComparison.OrdinalIgnoreCase);
        var entries = manifest.Files
            .Select(file => new ManagedFileEntry(NormalizeRelativePath(file.Path), file))
            .ToArray();
        var expectedPaths = new HashSet<string>(entries.Select(entry => entry.RelativePath), StringComparer.OrdinalIgnoreCase);
        var semaphore = new SemaphoreSlim(MaxParallelDownloads);
        var progressGate = new object();
        var nextProgressLog = DateTime.MinValue;
        var completed = 0;
        var reused = 0;
        var downloaded = 0;
        var skipped = 0;

        await Task.WhenAll(entries.Select(async entry =>
        {
            await semaphore.WaitAsync();
            try
            {
                var result = await EnsureFileAsync(
                    http,
                    gameDir,
                    entry.RelativePath,
                    entry.File,
                    canTrustInstalledFiles && previousManagedFiles.Contains(entry.RelativePath),
                    log,
                    entries.Length == 1 ? byteProgress : null);

                switch (result)
                {
                    case ManagedFileSyncResult.Reused:
                        Interlocked.Increment(ref reused);
                        break;
                    case ManagedFileSyncResult.Downloaded:
                        Interlocked.Increment(ref downloaded);
                        break;
                    case ManagedFileSyncResult.Skipped:
                        Interlocked.Increment(ref skipped);
                        break;
                }
            }
            finally
            {
                semaphore.Release();
                var done = Interlocked.Increment(ref completed);
                var now = DateTime.Now;
                var shouldLog = done == entries.Length;

                lock (progressGate)
                {
                    if (!shouldLog && now >= nextProgressLog)
                    {
                        shouldLog = true;
                        nextProgressLog = now.AddSeconds(1);
                    }
                }

                if (shouldLog)
                    log?.Invoke($"Pack: {done}/{entries.Length} arquivos verificados...");
            }
        }));

        var removed = 0;
        foreach (var previousPath in state.ManagedFiles.Where(path => !expectedPaths.Contains(path)).ToList())
        {
            var fullPath = ResolveGamePath(gameDir, previousPath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                log?.Invoke($"Removido arquivo antigo: {previousPath}");
                removed++;
            }
        }

        state.ManifestVersion = manifest.Version;
        state.ManagedFiles = expectedPaths.Order(StringComparer.OrdinalIgnoreCase).ToList();
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state, jsonOptions), Encoding.UTF8);

        log?.Invoke($"Pack sincronizado: {reused} mantidos, {downloaded} baixados, {removed} removidos, {skipped} ignorados.");
    }

    private static async Task<ManagedFileSyncResult> EnsureFileAsync(
        HttpClient http,
        string gameDir,
        string relativePath,
        ManagedFile file,
        bool trustExistingFile,
        Action<string>? log,
        Action<long, long>? byteProgress)
    {
        var targetPath = ResolveGamePath(gameDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        if (File.Exists(targetPath) && FileCanBeTrusted(targetPath, file, trustExistingFile))
            return ManagedFileSyncResult.Reused;

        if (File.Exists(targetPath) && await FileMatchesAsync(targetPath, file))
            return ManagedFileSyncResult.Reused;

        if (string.IsNullOrWhiteSpace(file.Url))
        {
            if (file.Required)
                throw new InvalidOperationException($"Arquivo obrigatorio sem URL no manifest: {relativePath}");

            log?.Invoke($"Ignorado sem URL: {relativePath}");
            return ManagedFileSyncResult.Skipped;
        }

        log?.Invoke($"Baixando: {relativePath}");
        var tempPath = $"{targetPath}.download";

        try
        {
            using var response = await http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var destination = File.Create(tempPath))
            {
                await CopyWithProgressAsync(source, destination, response.Content.Headers.ContentLength ?? file.Size, byteProgress);
            }

            if (!await FileMatchesAsync(tempPath, file))
                throw new InvalidOperationException($"Hash/tamanho invalido para {relativePath}. Confira o manifest.");

            File.Move(tempPath, targetPath, true);
            log?.Invoke($"Instalado: {relativePath}");
            return ManagedFileSyncResult.Downloaded;
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static async Task<bool> FileMatchesAsync(string path, ManagedFile file)
    {
        if (file.Size is not null)
        {
            var info = new FileInfo(path);
            if (info.Length != file.Size.Value)
                return false;
        }

        if (string.IsNullOrWhiteSpace(file.Sha256))
            return true;

        var hash = await ComputeSha256Async(path);
        return string.Equals(hash, file.Sha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool FileCanBeTrusted(string path, ManagedFile file, bool trustExistingFile)
    {
        if (!trustExistingFile)
            return false;

        if (file.Size is null)
            return true;

        var info = new FileInfo(path);
        return info.Length == file.Size.Value;
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
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

    private static string NormalizeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Manifest contem arquivo sem path.");

        var normalized = value.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains("../", StringComparison.Ordinal) || normalized.Equals("..", StringComparison.Ordinal))
            throw new InvalidOperationException($"Path invalido no manifest: {value}");

        if (Path.IsPathFullyQualified(normalized))
            throw new InvalidOperationException($"Path absoluto nao permitido no manifest: {value}");

        return normalized;
    }

    private static string ResolveGamePath(string gameDir, string relativePath)
    {
        var root = Path.GetFullPath(gameDir);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : $"{root}{Path.DirectorySeparatorChar}";

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path fora da pasta do jogo: {relativePath}");

        return fullPath;
    }

    private static async Task<LauncherState> LoadStateAsync(string statePath, JsonSerializerOptions jsonOptions)
    {
        if (!File.Exists(statePath))
            return new LauncherState();

        var json = await File.ReadAllTextAsync(statePath, Encoding.UTF8);
        return JsonSerializer.Deserialize<LauncherState>(json, jsonOptions) ?? new LauncherState();
    }
}

internal sealed record ManagedFileEntry(string RelativePath, ManagedFile File);

internal enum ManagedFileSyncResult
{
    Reused,
    Downloaded,
    Skipped
}

internal sealed class LauncherState
{
    public string ManifestVersion { get; set; } = "";
    public List<string> ManagedFiles { get; set; } = [];
}
