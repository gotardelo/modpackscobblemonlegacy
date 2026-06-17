using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;

internal static class Program
{
    private const string LauncherName = "Cobblemon Legacy";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = LauncherName;

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CobblemonLegacyLauncher", "1.0"));

        try
        {
            var settings = await LauncherSettings.LoadAsync(JsonOptions);
            var command = ResolveCommand(args);

            if (command == LauncherCommand.Menu)
                command = ReadMenuChoice(settings);

            if (command == LauncherCommand.Exit)
                return 0;

            var manifest = await ModpackManifestLoader.LoadAsync(http, settings.ManifestUrl, JsonOptions);
            var gameDir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.GameDirectory));

            PrintHeader(manifest, settings, gameDir);

            if (command == LauncherCommand.Paths)
            {
                PrintPaths(settings, gameDir);
                return 0;
            }

            var launcher = CreateMinecraftLauncher(gameDir);
            var fabricVersionId = await InstallOrUpdateAsync(http, launcher, manifest, gameDir);

            if (command == LauncherCommand.Install)
            {
                Console.WriteLine();
                Console.WriteLine("Instalacao concluida. Use o comando 'play' para abrir o Minecraft.");
                return 0;
            }

            await LaunchAsync(launcher, fabricVersionId, settings);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Falha no launcher:");
            Console.ResetColor();
            Console.WriteLine(ex.Message);
            return 1;
        }
    }

    private static MinecraftLauncher CreateMinecraftLauncher(string gameDir)
    {
        var launcher = new MinecraftLauncher(new MinecraftPath(gameDir));
        launcher.FileProgressChanged += (_, args) =>
        {
            if (args.TotalTasks <= 0)
            {
                Console.WriteLine($"Minecraft: {args.Name}");
                return;
            }

            Console.WriteLine($"Minecraft: {args.ProgressedTasks}/{args.TotalTasks} - {args.Name}");
        };
        launcher.ByteProgressChanged += (_, args) =>
        {
            if (args.TotalBytes > 0)
                Console.Write($"\rDownload: {FormatBytes(args.ProgressedBytes)} / {FormatBytes(args.TotalBytes)}   ");
        };
        return launcher;
    }

    private static async Task<string> InstallOrUpdateAsync(HttpClient http, MinecraftLauncher launcher, ModpackManifest manifest, string gameDir)
    {
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(Path.Combine(gameDir, "mods"));
        Directory.CreateDirectory(Path.Combine(gameDir, "resourcepacks"));
        Directory.CreateDirectory(Path.Combine(gameDir, "config"));

        Console.WriteLine();
        Console.WriteLine($"Instalando Minecraft {manifest.MinecraftVersion}...");
        await launcher.InstallAsync(manifest.MinecraftVersion);

        Console.WriteLine();
        Console.WriteLine("Preparando Fabric...");
        var fabricVersionId = await FabricProfileInstaller.InstallAsync(http, gameDir, manifest.MinecraftVersion, manifest.FabricLoaderVersion);

        Console.WriteLine($"Instalando bibliotecas da versao {fabricVersionId}...");
        await launcher.InstallAsync(fabricVersionId);

        Console.WriteLine();
        Console.WriteLine("Sincronizando mods/resourcepacks/config...");
        await ManagedFileSynchronizer.SyncAsync(http, gameDir, manifest, JsonOptions);

        return fabricVersionId;
    }

    private static async Task LaunchAsync(MinecraftLauncher launcher, string versionId, LauncherSettings settings)
    {
        Console.WriteLine();
        Console.WriteLine($"Abrindo {LauncherName} ({versionId})...");

        var process = await launcher.BuildProcessAsync(versionId, new MLaunchOption
        {
            Session = MSession.CreateOfflineSession(settings.OfflineUsername),
            MaximumRamMb = settings.MaximumRamMb
        });

        process.Start();
        Console.WriteLine($"Minecraft iniciado como '{settings.OfflineUsername}'.");
    }

    private static LauncherCommand ResolveCommand(string[] args)
    {
        if (args.Length == 0)
            return LauncherCommand.Menu;

        return args[0].Trim().ToLowerInvariant() switch
        {
            "play" or "jogar" or "launch" => LauncherCommand.Play,
            "install" or "instalar" or "update" or "atualizar" => LauncherCommand.Install,
            "paths" or "pastas" => LauncherCommand.Paths,
            "exit" or "sair" => LauncherCommand.Exit,
            _ => LauncherCommand.Menu
        };
    }

    private static LauncherCommand ReadMenuChoice(LauncherSettings settings)
    {
        Console.WriteLine($"{LauncherName} Launcher");
        Console.WriteLine();
        Console.WriteLine("1 - Instalar/atualizar e jogar");
        Console.WriteLine("2 - Apenas instalar/atualizar");
        Console.WriteLine("3 - Mostrar pastas/configuracao");
        Console.WriteLine("0 - Sair");
        Console.WriteLine();
        Console.Write($"Escolha uma opcao [1] - jogador: {settings.OfflineUsername}: ");

        return Console.ReadLine()?.Trim() switch
        {
            "0" => LauncherCommand.Exit,
            "2" => LauncherCommand.Install,
            "3" => LauncherCommand.Paths,
            _ => LauncherCommand.Play
        };
    }

    private static void PrintHeader(ModpackManifest manifest, LauncherSettings settings, string gameDir)
    {
        Console.WriteLine();
        Console.WriteLine($"{manifest.Name} v{manifest.Version}");
        Console.WriteLine($"Minecraft: {manifest.MinecraftVersion}");
        Console.WriteLine($"Fabric: {manifest.FabricLoaderVersion}");
        Console.WriteLine($"RAM maxima: {settings.MaximumRamMb} MB");
        Console.WriteLine($"Pasta do jogo: {gameDir}");
    }

    private static void PrintPaths(LauncherSettings settings, string gameDir)
    {
        Console.WriteLine();
        Console.WriteLine($"Configuracao: {LauncherSettings.SettingsPath}");
        Console.WriteLine($"Manifest remoto: {settings.ManifestUrl}");
        Console.WriteLine($"Pasta do jogo: {gameDir}");
        Console.WriteLine($"Mods: {Path.Combine(gameDir, "mods")}");
        Console.WriteLine($"Resourcepacks: {Path.Combine(gameDir, "resourcepacks")}");
    }

    private static string FormatBytes(long bytes)
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

internal enum LauncherCommand
{
    Menu,
    Play,
    Install,
    Paths,
    Exit
}

internal sealed class LauncherSettings
{
    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CobblemonLegacyLauncher",
        "launcher.settings.json");

    public string ManifestUrl { get; set; } = ProgramDefaults.ManifestUrl;
    public string GameDirectory { get; set; } = "%APPDATA%\\.cobblemonlegacy";
    public string OfflineUsername { get; set; } = "Player";
    public int MaximumRamMb { get; set; } = 4096;

    public static async Task<LauncherSettings> LoadAsync(JsonSerializerOptions jsonOptions)
    {
        if (!File.Exists(SettingsPath))
        {
            var settings = new LauncherSettings();
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            await File.WriteAllTextAsync(SettingsPath, JsonSerializer.Serialize(settings, jsonOptions), Encoding.UTF8);
            return settings;
        }

        var json = await File.ReadAllTextAsync(SettingsPath, Encoding.UTF8);
        var loaded = JsonSerializer.Deserialize<LauncherSettings>(json, jsonOptions) ?? new LauncherSettings();
        loaded.MaximumRamMb = Math.Max(1024, loaded.MaximumRamMb);
        loaded.OfflineUsername = string.IsNullOrWhiteSpace(loaded.OfflineUsername) ? "Player" : loaded.OfflineUsername.Trim();
        loaded.ManifestUrl = string.IsNullOrWhiteSpace(loaded.ManifestUrl) ? ProgramDefaults.ManifestUrl : loaded.ManifestUrl.Trim();
        loaded.GameDirectory = string.IsNullOrWhiteSpace(loaded.GameDirectory) ? "%APPDATA%\\.cobblemonlegacy" : loaded.GameDirectory.Trim();
        return loaded;
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
    public static async Task<ModpackManifest> LoadAsync(HttpClient http, string manifestLocation, JsonSerializerOptions jsonOptions)
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
                Console.WriteLine($"Baixando manifest: {manifestLocation}");
                var json = await http.GetStringAsync(manifestLocation);
                return Normalize(Deserialize(json, jsonOptions));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Nao foi possivel baixar o manifest remoto: {ex.Message}");
                Console.WriteLine("Tentando manifest local...");
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
                Console.WriteLine($"Usando manifest local: {fallback}");
                return Normalize(Deserialize(await File.ReadAllTextAsync(fallback, Encoding.UTF8), jsonOptions));
            }
        }

        throw new InvalidOperationException("Nenhum manifest encontrado. Ajuste launcher.settings.json ou coloque manifest.json ao lado do projeto/executavel.");
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
}

internal static class FabricProfileInstaller
{
    public static async Task<string> InstallAsync(HttpClient http, string gameDir, string minecraftVersion, string loaderVersion)
    {
        var resolvedLoaderVersion = await ResolveLoaderVersionAsync(http, gameDir, minecraftVersion, loaderVersion);
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
            Console.WriteLine($"Fabric {resolvedLoaderVersion} instalado como {versionId}.");
            return versionId;
        }
        catch (Exception ex) when (File.Exists(expectedJsonPath))
        {
            Console.WriteLine($"Nao foi possivel atualizar o perfil Fabric ({ex.Message}). Usando perfil local.");
            return expectedVersionId;
        }
    }

    private static async Task<string> ResolveLoaderVersionAsync(HttpClient http, string gameDir, string minecraftVersion, string loaderVersion)
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
                Console.WriteLine($"Nao foi possivel consultar Fabric latest ({ex.Message}). Usando {installed.LoaderVersion} local.");
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
    private const string StateFileName = ".cobblemonlegacy-launcher-state.json";

    public static async Task SyncAsync(HttpClient http, string gameDir, ModpackManifest manifest, JsonSerializerOptions jsonOptions)
    {
        var statePath = Path.Combine(gameDir, StateFileName);
        var state = await LoadStateAsync(statePath, jsonOptions);
        var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in manifest.Files)
        {
            var relativePath = NormalizeRelativePath(file.Path);
            expectedPaths.Add(relativePath);
            await EnsureFileAsync(http, gameDir, relativePath, file);
        }

        foreach (var previousPath in state.ManagedFiles.Where(path => !expectedPaths.Contains(path)).ToList())
        {
            var fullPath = ResolveGamePath(gameDir, previousPath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                Console.WriteLine($"Removido arquivo antigo: {previousPath}");
            }
        }

        state.ManifestVersion = manifest.Version;
        state.ManagedFiles = expectedPaths.Order(StringComparer.OrdinalIgnoreCase).ToList();
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state, jsonOptions), Encoding.UTF8);

        Console.WriteLine($"{expectedPaths.Count} arquivo(s) gerenciado(s) sincronizado(s).");
    }

    private static async Task EnsureFileAsync(HttpClient http, string gameDir, string relativePath, ManagedFile file)
    {
        var targetPath = ResolveGamePath(gameDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        if (File.Exists(targetPath) && await FileMatchesAsync(targetPath, file))
        {
            Console.WriteLine($"OK: {relativePath}");
            return;
        }

        if (string.IsNullOrWhiteSpace(file.Url))
        {
            if (file.Required)
                throw new InvalidOperationException($"Arquivo obrigatorio sem URL no manifest: {relativePath}");

            Console.WriteLine($"Ignorado sem URL: {relativePath}");
            return;
        }

        Console.WriteLine($"Baixando: {relativePath}");
        var tempPath = $"{targetPath}.download";

        try
        {
            using var response = await http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var destination = File.Create(tempPath))
            {
                await CopyWithProgressAsync(source, destination, response.Content.Headers.ContentLength ?? file.Size, relativePath);
            }

            if (!await FileMatchesAsync(tempPath, file))
                throw new InvalidOperationException($"Hash/tamanho invalido para {relativePath}. Confira o manifest.");

            File.Move(tempPath, targetPath, true);
            Console.WriteLine($"Instalado: {relativePath}");
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

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task CopyWithProgressAsync(Stream source, Stream destination, long? totalBytes, string relativePath)
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
                Console.Write($"\r{relativePath}: {FormatPercent(copied, totalBytes.Value)}   ");
        }

        if (totalBytes is > 0)
            Console.WriteLine();
    }

    private static string FormatPercent(long current, long total)
    {
        return $"{current * 100d / total:0.0}%";
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

internal sealed class LauncherState
{
    public string ManifestVersion { get; set; } = "";
    public List<string> ManagedFiles { get; set; } = [];
}
