using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Runtime.InteropServices;
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
    public const string LauncherVersion = "1.4.3";
    public const string ServerIp = "enx-cirion-16.enx.host:10068";
    public const string ServerHost = "Enxada Host";
    private const int StaleGameProcessSeconds = 30;
    private static readonly Regex SensitiveLaunchArgumentRegex = new(
        @"(--(?:accessToken|uuid|xuid|clientId)\s+)(""[^""]*""|\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MemoryJvmArgumentRegex = new(
        @"(^|\s)-Xm[sx]\S*",
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
        string resourcepackProfile,
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

        if (IsVersionInstalled(gameDir, fabricVersionId) && await FabricProfileInstaller.HasRequiredLibrariesAsync(gameDir, fabricVersionId, log))
        {
            log?.Invoke($"Fabric {fabricVersionId} ja instalado e validado.");
        }
        else
        {
            log?.Invoke($"Instalando bibliotecas da versao {fabricVersionId}...");
            await launcher.InstallAsync(fabricVersionId);
        }

        await FabricProfileInstaller.RepairLibrariesAsync(http, gameDir, fabricVersionId, log);

        log?.Invoke("Sincronizando mods, resourcepacks e configs...");
        await ManagedFileSynchronizer.SyncAsync(http, gameDir, manifest, JsonOptions, resourcepackProfile, log, byteProgress);

        return fabricVersionId;
    }

    public static async Task<string> RepairInstallationAsync(
        HttpClient http,
        MinecraftLauncher launcher,
        ModpackManifest manifest,
        string gameDir,
        string resourcepackProfile,
        Action<string>? log = null,
        Action<long, long>? byteProgress = null)
    {
        Directory.CreateDirectory(gameDir);
        log?.Invoke("Modo reparo iniciado. Seus mundos e configuracoes pessoais serao preservados.");

        ResetManagedPackState(gameDir, log);
        RemoveFabricInstall(gameDir, manifest.MinecraftVersion, log);
        ClearMinecraftProcessLog();

        await BackupUserConfigurationAsync(gameDir, log);

        var versionId = await InstallOrUpdateAsync(http, launcher, manifest, gameDir, resourcepackProfile, log, byteProgress);
        log?.Invoke("Reparo concluido.");
        return versionId;
    }

    public static async Task<PackReadiness> CheckPackReadinessAsync(
        string gameDir,
        ModpackManifest manifest,
        JsonSerializerOptions jsonOptions,
        string resourcepackProfile)
    {
        if (!IsVersionInstalled(gameDir, manifest.MinecraftVersion))
            return new PackReadiness(false, $"Minecraft {manifest.MinecraftVersion} precisa ser instalado.");

        if (!FabricProfileInstaller.IsInstalled(gameDir, manifest.MinecraftVersion, manifest.FabricLoaderVersion))
            return new PackReadiness(false, "Fabric precisa ser instalado.");

        var fabricVersion = FabricProfileInstaller.FindInstalledVersionId(gameDir, manifest.MinecraftVersion, manifest.FabricLoaderVersion);
        if (fabricVersion is null || !await FabricProfileInstaller.HasRequiredLibrariesAsync(gameDir, fabricVersion))
            return new PackReadiness(false, "Bibliotecas do Fabric precisam ser reparadas.");

        if (!await ManagedFileSynchronizer.IsSynchronizedAsync(gameDir, manifest, jsonOptions, resourcepackProfile))
            return new PackReadiness(false, "Pack precisa ser atualizado.");

        return new PackReadiness(true, "Pronto para jogar.");
    }

    private static bool IsVersionInstalled(string gameDir, string versionId)
    {
        return File.Exists(Path.Combine(gameDir, "versions", versionId, $"{versionId}.json"));
    }

    private static void ResetManagedPackState(string gameDir, Action<string>? log)
    {
        var statePath = Path.Combine(gameDir, ManagedFileSynchronizer.StateFileName);
        if (File.Exists(statePath))
        {
            File.Delete(statePath);
            log?.Invoke("Estado do pack resetado para nova verificacao completa.");
        }
    }

    private static void RemoveFabricInstall(string gameDir, string minecraftVersion, Action<string>? log)
    {
        var versionsDir = Path.Combine(gameDir, "versions");
        if (Directory.Exists(versionsDir))
        {
            foreach (var directory in Directory.EnumerateDirectories(versionsDir, $"fabric-loader-*-{minecraftVersion}"))
                DeleteDirectoryInsideGameDir(gameDir, directory, log);
        }

        DeleteDirectoryInsideGameDir(gameDir, Path.Combine(gameDir, "libraries", "net", "fabricmc"), log);
    }

    private static void DeleteDirectoryInsideGameDir(string gameDir, string directory, Action<string>? log)
    {
        if (!Directory.Exists(directory))
            return;

        var root = Path.GetFullPath(gameDir);
        var fullDirectory = Path.GetFullPath(directory);
        if (!IsInsideDirectory(fullDirectory, root))
            throw new InvalidOperationException($"Reparo bloqueado fora da pasta do jogo: {fullDirectory}");

        Directory.Delete(fullDirectory, recursive: true);
        log?.Invoke($"Reparo removeu cache: {Path.GetRelativePath(root, fullDirectory)}");
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

        var maximumRamMb = ResolveMaximumRamMb(settings.MaximumRamMb, log);
        var launchOptions = new MLaunchOption
        {
            Session = session,
            MaximumRamMb = maximumRamMb,
            MinimumRamMb = Math.Min(1024, maximumRamMb),
            ScreenWidth = settings.WindowWidth,
            ScreenHeight = settings.WindowHeight,
            FullScreen = settings.FullScreen,
            ExtraJvmArguments = BuildPerformanceJvmArguments(settings),
            GameLauncherName = "CobblemonLegacy",
            GameLauncherVersion = LauncherVersion
        };

        var javaPath = ResolveJavaPath(settings, log);
        if (!string.IsNullOrWhiteSpace(javaPath))
            launchOptions.JavaPath = javaPath;

        var process = await launcher.BuildProcessAsync(versionId, launchOptions);

        ConfigureMinecraftProcess(process, log);
        await WriteLaunchDiagnosticsAsync(process.StartInfo);
        ClearMinecraftProcessLog();

        if (!process.Start())
            throw new InvalidOperationException("O processo do Minecraft nao iniciou.");

        TryBoostMinecraftProcess(process, log);
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

    private static void TryBoostMinecraftProcess(System.Diagnostics.Process process, Action<string>? log)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.AboveNormal;
            log?.Invoke("Minecraft em prioridade alta para abrir mais rapido.");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Nao foi possivel ajustar prioridade do Minecraft: {ex.Message}");
        }
    }

    public static async Task<string> GetLatestMinecraftLogHintAsync(string gameDir)
    {
        var latestLogPath = Path.Combine(gameDir, "logs", "latest.log");
        var processLogPath = Path.Combine(Path.GetDirectoryName(LauncherSettings.SettingsPath)!, "minecraft-process.log");
        var javaCrashLogPath = FindNewestJavaCrashLog(gameDir);

        try
        {
            if (File.Exists(latestLogPath))
                return await ReadTailHintAsync(latestLogPath, "Ultimas linhas do latest.log");

            if (File.Exists(processLogPath))
                return await ReadTailHintAsync(processLogPath, "Saida do Java antes do Minecraft abrir");

            if (javaCrashLogPath is not null)
                return await ReadTailHintAsync(javaCrashLogPath, "Crash log do Java");

            return
                $"O Minecraft ainda nao criou latest.log em: {latestLogPath}{Environment.NewLine}" +
                $"Tambem nao encontrei saida do Java em: {processLogPath}";
        }
        catch (Exception ex)
        {
            return $"Nao foi possivel ler os logs do Minecraft: {ex.Message}";
        }
    }

    private static async Task<string> ReadTailHintAsync(string path, string title)
    {
        var lines = await File.ReadAllLinesAsync(path, Encoding.UTF8);
        var tail = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .TakeLast(12);

        return $"{title} ({path}):{Environment.NewLine}{string.Join(Environment.NewLine, tail)}";
    }

    private static string? FindNewestJavaCrashLog(string gameDir)
    {
        try
        {
            var searchRoots = new[]
            {
                gameDir,
                Path.GetDirectoryName(LauncherSettings.SettingsPath)!
            };

            return searchRoots
                .Where(Directory.Exists)
                .SelectMany(root => Directory.EnumerateFiles(root, "hs_err_pid*.log", SearchOption.TopDirectoryOnly))
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault()
                ?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static int ResolveMaximumRamMb(int requestedRamMb, Action<string>? log = null)
    {
        requestedRamMb = Math.Clamp(requestedRamMb, 2_048, 8_192);
        var memory = TryGetPhysicalMemoryMb();
        if (memory is null)
            return requestedRamMb;

        var recommendedRamMb = GetRecommendedMaximumRamMb(memory.TotalMb);
        var safeAvailableRamMb = Math.Max(2_048, memory.AvailableMb - 1_024);
        var resolvedRamMb = Math.Clamp(
            Math.Min(requestedRamMb, Math.Min(recommendedRamMb, safeAvailableRamMb)),
            2_048,
            requestedRamMb);

        if (resolvedRamMb != requestedRamMb)
        {
            log?.Invoke(
                $"RAM ajustada automaticamente: {requestedRamMb} MB -> {resolvedRamMb} MB " +
                $"({memory.TotalMb} MB fisicos, {memory.AvailableMb} MB livres).");
        }
        else
        {
            log?.Invoke($"RAM do Minecraft: {resolvedRamMb} MB ({memory.TotalMb} MB fisicos, {memory.AvailableMb} MB livres).");
        }

        return resolvedRamMb;
    }

    public static int GetRecommendedMaximumRamMb()
    {
        var memory = TryGetPhysicalMemoryMb();
        return memory is null ? 4_096 : GetRecommendedMaximumRamMb(memory.TotalMb);
    }

    public static SystemMemorySnapshot? GetSystemMemorySnapshot()
    {
        var memory = TryGetPhysicalMemoryMb();
        return memory is null ? null : new SystemMemorySnapshot(memory.TotalMb, memory.AvailableMb);
    }

    public static PerformanceTier GetPerformanceTier()
    {
        var memory = TryGetPhysicalMemoryMb();
        if (memory is null)
            return PerformanceTier.Standard;

        if (memory.TotalMb < 9_000)
            return PerformanceTier.LowEnd;

        if (memory.TotalMb < 13_000)
            return PerformanceTier.Balanced;

        return PerformanceTier.Standard;
    }

    public static int GetRecommendedParallelDownloads()
    {
        return GetPerformanceTier() switch
        {
            PerformanceTier.LowEnd => 2,
            PerformanceTier.Balanced => 3,
            _ => 4
        };
    }

    public static GameWindowSize GetRecommendedGameWindowSize()
    {
        var screen = TryGetPrimaryScreenSize();
        var tier = GetPerformanceTier();
        var target = tier switch
        {
            PerformanceTier.LowEnd => new GameWindowSize(854, 480),
            PerformanceTier.Balanced => new GameWindowSize(1280, 720),
            _ => new GameWindowSize(1366, 768)
        };

        if (screen is null)
            return target;

        var maxWidth = Math.Max(640, (int)(screen.Value.Width * 0.82));
        var maxHeight = Math.Max(360, (int)(screen.Value.Height * 0.78));
        var width = Math.Min(target.Width, maxWidth);
        var height = Math.Min(target.Height, maxHeight);

        if (width < target.Width || height < target.Height)
        {
            var scale = Math.Min(width / (double)target.Width, height / (double)target.Height);
            width = Math.Max(640, RoundToEven((int)(target.Width * scale)));
            height = Math.Max(360, RoundToEven((int)(target.Height * scale)));
        }

        return new GameWindowSize(width, height);
    }

    private static int GetRecommendedMaximumRamMb(int totalMemoryMb)
    {
        return totalMemoryMb switch
        {
            >= 24_576 => 8_192,
            >= 16_384 => 6_144,
            >= 12_288 => 5_120,
            >= 8_192 => 3_072,
            _ => 2_048
        };
    }

    private static MArgument[] BuildPerformanceJvmArguments(LauncherSettings settings)
    {
        var arguments = new List<MArgument>
        {
            new("-XX:+UseG1GC"),
            new("-XX:+ParallelRefProcEnabled"),
            new("-XX:MaxGCPauseMillis=200"),
            new("-XX:+DisableExplicitGC"),
            new("-Djava.net.preferIPv4Stack=true"),
            new("-Dfile.encoding=UTF-8"),
            new("-Dsun.rmi.dgc.server.gcInterval=2147483646"),
            new("-Dsun.rmi.dgc.client.gcInterval=2147483646")
        };

        if (settings.CompatibilityMode)
        {
            arguments.Add(new("-Dio.netty.tryReflectionSetAccessible=true"));
            arguments.Add(new("-Djoml.fastmath=false"));
        }

        var customArguments = RemoveMemoryJvmArguments(settings.ExtraJvmArguments);
        if (!string.IsNullOrWhiteSpace(customArguments))
            arguments.Add(MArgument.FromCommandLine(customArguments));

        return arguments.ToArray();
    }

    private static string RemoveMemoryJvmArguments(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return MemoryJvmArgumentRegex.Replace(value, "$1").Trim();
    }

    private static string? ResolveJavaPath(LauncherSettings settings, Action<string>? log = null)
    {
        if (settings.UseIntegratedJava || string.IsNullOrWhiteSpace(settings.JavaPath))
            return null;

        var javaPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.JavaPath.Trim()));
        if (!File.Exists(javaPath))
            throw new InvalidOperationException($"Java customizado nao encontrado: {javaPath}");

        log?.Invoke($"Java customizado selecionado: {javaPath}");
        return javaPath;
    }

    private static PhysicalMemoryInfo? TryGetPhysicalMemoryMb()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status))
            return null;

        return new PhysicalMemoryInfo(
            (int)Math.Max(1, status.TotalPhys / 1024 / 1024),
            (int)Math.Max(1, status.AvailPhys / 1024 / 1024));
    }

    private sealed record PhysicalMemoryInfo(int TotalMb, int AvailableMb);

    private static GameWindowSize? TryGetPrimaryScreenSize()
    {
        var width = GetSystemMetrics(0);
        var height = GetSystemMetrics(1);
        return width > 0 && height > 0 ? new GameWindowSize(width, height) : null;
    }

    private static int RoundToEven(int value)
    {
        return value % 2 == 0 ? value : value - 1;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    private static void ConfigureMinecraftProcess(System.Diagnostics.Process process, Action<string>? log)
    {
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.WindowStyle = ProcessWindowStyle.Normal;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        log?.Invoke($"Java: {Path.GetFileName(process.StartInfo.FileName)}");
    }

    private static void BeginMinecraftOutputRead(System.Diagnostics.Process process, Action<string>? log)
    {
        if (!process.StartInfo.RedirectStandardOutput && !process.StartInfo.RedirectStandardError)
            return;

        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                WriteMinecraftProcessLog(args.Data);
                log?.Invoke($"Minecraft: {args.Data}");
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                WriteMinecraftProcessLog(args.Data);
                log?.Invoke($"Minecraft: {args.Data}");
            }
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

    private static void WriteMinecraftProcessLog(string line)
    {
        try
        {
            File.AppendAllText(GetMinecraftProcessLogPath(), $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // Process output is useful for support, but should never block launching.
        }
    }

    private static void ClearMinecraftProcessLog()
    {
        try
        {
            File.WriteAllText(GetMinecraftProcessLogPath(), "", Encoding.UTF8);
        }
        catch
        {
            // Diagnostics only.
        }
    }

    private static string GetMinecraftProcessLogPath()
    {
        var diagnosticsDir = Path.GetDirectoryName(LauncherSettings.SettingsPath)!;
        Directory.CreateDirectory(diagnosticsDir);
        return Path.Combine(diagnosticsDir, "minecraft-process.log");
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

    public static string TelemetryPath { get; } = Path.Combine(
        Path.GetDirectoryName(LauncherSettings.SettingsPath)!,
        "telemetry.jsonl");

    public static void WriteTelemetry(string eventName, IReadOnlyDictionary<string, object?>? data = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TelemetryPath)!);
            if (File.Exists(TelemetryPath) && new FileInfo(TelemetryPath).Length > 1024 * 1024)
                File.WriteAllText(TelemetryPath, "", Encoding.UTF8);

            var payload = new Dictionary<string, object?>
            {
                ["time"] = DateTimeOffset.Now,
                ["event"] = eventName,
                ["launcherVersion"] = LauncherVersion
            };

            if (data is not null)
            {
                foreach (var item in data)
                    payload[item.Key] = item.Value;
            }

            File.AppendAllText(TelemetryPath, JsonSerializer.Serialize(payload, TelemetryJsonOptions) + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Local telemetry is diagnostic only.
        }
    }

    public static bool HasProcessWindow(System.Diagnostics.Process process)
    {
        try
        {
            if (process.HasExited)
                return false;

            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
                return true;

            var processId = process.Id;
            var hasWindow = false;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var windowProcessId);
                if (windowProcessId == processId && IsWindowVisible(window))
                {
                    hasWindow = true;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return hasWindow;
        }
        catch
        {
            return false;
        }
    }

    public static void TrimCurrentProcessMemory()
    {
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            EmptyWorkingSet(currentProcess.Handle);
        }
        catch
        {
            // Best-effort only. The launcher must never fail because memory trimming failed.
        }
    }

    private static readonly JsonSerializerOptions TelemetryJsonOptions = new(JsonSerializerDefaults.Web);

    public static Task<string?> BackupUserConfigurationAsync(string gameDir, Action<string>? log = null)
    {
        try
        {
            var root = Path.GetFullPath(gameDir);
            var backupDir = Path.Combine(root, "backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var copied = 0;

            copied += CopyBackupFile(root, backupDir, "options.txt");
            copied += CopyBackupFile(root, backupDir, "optionsof.txt");
            copied += CopyBackupFile(root, backupDir, "servers.dat");
            copied += CopyBackupDirectory(root, backupDir, "config", maxFiles: 250, maxBytes: 50L * 1024 * 1024);

            if (copied == 0)
                return Task.FromResult<string?>(null);

            log?.Invoke($"Backup de configuracoes criado: {backupDir}");
            WriteTelemetry("backup_created", new Dictionary<string, object?>
            {
                ["path"] = backupDir,
                ["files"] = copied
            });
            return Task.FromResult<string?>(backupDir);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Nao foi possivel criar backup de configuracoes: {ex.Message}");
            WriteTelemetry("backup_failed", new Dictionary<string, object?> { ["error"] = ex.Message });
            return Task.FromResult<string?>(null);
        }
    }

    private static int CopyBackupFile(string gameDir, string backupDir, string relativePath)
    {
        var source = Path.Combine(gameDir, relativePath);
        if (!File.Exists(source))
            return 0;

        var target = Path.Combine(backupDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
        return 1;
    }

    private static int CopyBackupDirectory(string gameDir, string backupDir, string relativeDirectory, int maxFiles, long maxBytes)
    {
        var sourceDir = Path.Combine(gameDir, relativeDirectory);
        if (!Directory.Exists(sourceDir))
            return 0;

        var root = Path.GetFullPath(gameDir);
        var copied = 0;
        long bytes = 0;

        foreach (var source in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            if (copied >= maxFiles || bytes >= maxBytes)
                break;

            var fullSource = Path.GetFullPath(source);
            if (!IsInsideDirectory(fullSource, root))
                continue;

            var info = new FileInfo(fullSource);
            if (info.Length <= 0 || bytes + info.Length > maxBytes)
                continue;

            var relativePath = Path.GetRelativePath(root, fullSource);
            var target = Path.Combine(backupDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(fullSource, target, overwrite: true);
            copied++;
            bytes += info.Length;
        }

        return copied;
    }

    public static async Task<CrashReportResult> CreateCrashReportAsync(
        LauncherSettings? settings,
        string visibleLog,
        string lastStatus)
    {
        var diagnosticsDir = Path.GetDirectoryName(LauncherSettings.SettingsPath)!;
        var reportsDir = Path.Combine(diagnosticsDir, "crash-reports");
        Directory.CreateDirectory(reportsDir);

        var gameDir = settings is null
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables("%APPDATA%\\.cobblemonlegacy"))
            : ExpandGameDirectory(settings);
        var reportPath = Path.Combine(reportsDir, $"CobblemonLegacy-Report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var report = new StringBuilder();

        AppendReportHeader(report, settings, gameDir, lastStatus);
        await AppendKnownLogFilesAsync(report, diagnosticsDir, gameDir);
        AppendSection(report, "Launcher UI visivel", string.IsNullOrWhiteSpace(visibleLog) ? "Sem log visivel." : visibleLog);

        await File.WriteAllTextAsync(reportPath, report.ToString(), Encoding.UTF8);
        return new CrashReportResult(reportPath, report.ToString());
    }

    public static async Task<SupportPackageResult> CreateSupportPackageAsync(
        LauncherSettings? settings,
        string visibleLog,
        string lastStatus)
    {
        var report = await CreateCrashReportAsync(settings, visibleLog, lastStatus);
        var diagnosticsDir = Path.GetDirectoryName(LauncherSettings.SettingsPath)!;
        var reportsDir = Path.Combine(diagnosticsDir, "crash-reports");
        var gameDir = settings is null
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables("%APPDATA%\\.cobblemonlegacy"))
            : ExpandGameDirectory(settings);
        var zipPath = Path.Combine(reportsDir, $"CobblemonLegacy-Support-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddFileToArchiveIfExists(archive, report.Path, "CobblemonLegacy-Report.txt");
            AddFileToArchiveIfExists(archive, LauncherSettings.SettingsPath, "launcher/launcher.settings.json");
            AddFileToArchiveIfExists(archive, Path.Combine(diagnosticsDir, "launcher-ui.log"), "launcher/launcher-ui.log");
            AddFileToArchiveIfExists(archive, Path.Combine(diagnosticsDir, "launcher.log"), "launcher/launcher.log");
            AddFileToArchiveIfExists(archive, Path.Combine(diagnosticsDir, "minecraft-process.log"), "launcher/minecraft-process.log");
            AddFileToArchiveIfExists(archive, Path.Combine(diagnosticsDir, "last-launch-command.txt"), "launcher/last-launch-command.txt");
            AddFileToArchiveIfExists(archive, TelemetryPath, "launcher/telemetry.jsonl");
            AddFileToArchiveIfExists(archive, ModpackManifestLoader.CachePath, "launcher/manifest-cache.json");
            AddFileToArchiveIfExists(archive, Path.Combine(gameDir, "logs", "latest.log"), "minecraft/latest.log");

            var latestCrash = FindNewestFile(Path.Combine(gameDir, "crash-reports"), "crash-*.txt");
            AddFileToArchiveIfExists(archive, latestCrash, "minecraft/latest-crash-report.txt");

            var latestJavaCrash = FindNewestJavaCrashLog(gameDir);
            AddFileToArchiveIfExists(archive, latestJavaCrash, "minecraft/latest-java-crash.log");
        }

        WriteTelemetry("support_package_created", new Dictionary<string, object?>
        {
            ["report"] = report.Path,
            ["zip"] = zipPath
        });

        return new SupportPackageResult(report.Path, zipPath, report.Text);
    }

    public static async Task<DiagnosticsSnapshot> CreateDiagnosticsSnapshotAsync(
        LauncherSettings? settings,
        ModpackManifest? manifest,
        string lastStatus)
    {
        var items = new List<DiagnosticItem>();
        var memory = TryGetPhysicalMemoryMb();
        var gameDir = settings is null
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables("%APPDATA%\\.cobblemonlegacy"))
            : ExpandGameDirectory(settings);

        AddDiagnostic(items, "Launcher", LauncherVersion, DiagnosticState.Ok);
        AddDiagnostic(items, "Servidor", ServerIp, DiagnosticState.Ok);
        AddDiagnostic(items, "Windows", Environment.OSVersion.ToString(), DiagnosticState.Ok);
        AddDiagnostic(items, ".NET", Environment.Version.ToString(), DiagnosticState.Ok);
        AddDiagnostic(items, "Sistema 64-bit", Environment.Is64BitOperatingSystem ? "Sim" : "Nao", Environment.Is64BitOperatingSystem ? DiagnosticState.Ok : DiagnosticState.Warning);
        AddDiagnostic(items, "Processo 64-bit", Environment.Is64BitProcess ? "Sim" : "Nao", Environment.Is64BitProcess ? DiagnosticState.Ok : DiagnosticState.Warning);
        AddDiagnostic(items, "Memoria fisica", memory is null ? "Desconhecida" : $"{memory.TotalMb} MB total / {memory.AvailableMb} MB livre", memory is null ? DiagnosticState.Warning : DiagnosticState.Ok);
        AddDiagnostic(items, "Pasta do jogo", gameDir, Directory.Exists(gameDir) ? DiagnosticState.Ok : DiagnosticState.Warning);
        AddDiagnostic(items, "Configuracoes", LauncherSettings.SettingsPath, File.Exists(LauncherSettings.SettingsPath) ? DiagnosticState.Ok : DiagnosticState.Warning);
        AddDiagnostic(items, "Perfil", settings?.AuthMode switch
        {
            AuthModes.Microsoft => string.IsNullOrWhiteSpace(settings.MicrosoftUsername) ? "Microsoft pendente" : $"Microsoft: {settings.MicrosoftUsername}",
            AuthModes.Offline => $"Offline: {settings.OfflineUsername}",
            _ => "Nao escolhido"
        }, string.IsNullOrWhiteSpace(settings?.AuthMode) ? DiagnosticState.Warning : DiagnosticState.Ok);
        AddDiagnostic(items, "RAM configurada", settings is null ? "Desconhecida" : $"{settings.MaximumRamMb} MB", DiagnosticState.Ok);
        AddDiagnostic(items, "Modo desempenho", settings?.PerformanceProfile ?? PerformanceProfiles.Auto, DiagnosticState.Ok);
        AddDiagnostic(items, "Resourcepacks", ResourcepackProfiles.ToDisplayName(settings?.ResourcepackProfile), DiagnosticState.Ok);
        AddDiagnostic(items, "Java", settings is null || settings.UseIntegratedJava ? "Runtime integrado" : settings.JavaPath, settings is not null && !settings.UseIntegratedJava && !File.Exists(settings.JavaPath) ? DiagnosticState.Error : DiagnosticState.Ok);
        AddDiagnostic(items, "Manifest", manifest is null ? "Nao carregado" : $"{manifest.Version} / {manifest.Files.Count} arquivos", manifest is null ? DiagnosticState.Warning : DiagnosticState.Ok);
        AddDiagnostic(items, "Telemetria local", TelemetryPath, File.Exists(TelemetryPath) ? DiagnosticState.Ok : DiagnosticState.Warning);

        if (settings is not null && manifest is not null)
        {
            try
            {
                var readiness = await CheckPackReadinessAsync(gameDir, manifest, JsonOptions, settings.ResourcepackProfile);
                AddDiagnostic(items, "Integridade do pack", readiness.Message, readiness.IsReady ? DiagnosticState.Ok : DiagnosticState.Warning);

                var fabricVersion = FabricProfileInstaller.FindInstalledVersionId(gameDir, manifest.MinecraftVersion, manifest.FabricLoaderVersion);
                if (fabricVersion is null)
                {
                    AddDiagnostic(items, "Fabric", "Nao instalado", DiagnosticState.Warning);
                }
                else
                {
                    var librariesOk = await FabricProfileInstaller.HasRequiredLibrariesAsync(gameDir, fabricVersion);
                    AddDiagnostic(items, "Fabric", $"{fabricVersion} / bibliotecas {(librariesOk ? "ok" : "com problema")}", librariesOk ? DiagnosticState.Ok : DiagnosticState.Error);
                }
            }
            catch (Exception ex)
            {
                AddDiagnostic(items, "Integridade do pack", ex.Message, DiagnosticState.Error);
            }
        }

        AddDiagnostic(items, "Ultimo latest.log", Path.Combine(gameDir, "logs", "latest.log"), File.Exists(Path.Combine(gameDir, "logs", "latest.log")) ? DiagnosticState.Ok : DiagnosticState.Warning);
        AddDiagnostic(items, "Ultimo status", lastStatus, DiagnosticState.Ok);

        return new DiagnosticsSnapshot(DateTimeOffset.Now, items);
    }

    private static void AddDiagnostic(List<DiagnosticItem> items, string name, string value, DiagnosticState state)
    {
        items.Add(new DiagnosticItem(name, value, state));
    }

    private static void AddFileToArchiveIfExists(ZipArchive archive, string? sourcePath, string entryName)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return;

        try
        {
            archive.CreateEntryFromFile(sourcePath, entryName, CompressionLevel.Fastest);
        }
        catch
        {
            // A support package should still be useful even if one log is locked.
        }
    }

    private static void AppendReportHeader(StringBuilder report, LauncherSettings? settings, string gameDir, string lastStatus)
    {
        var memory = TryGetPhysicalMemoryMb();
        report.AppendLine("COBBLEMON LEGACY - RELATORIO DE ERRO");
        report.AppendLine($"Gerado em: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"Launcher: {LauncherVersion}");
        report.AppendLine($"Servidor: {ServerIp}");
        report.AppendLine($"Windows: {Environment.OSVersion}");
        report.AppendLine($".NET: {Environment.Version}");
        report.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
        report.AppendLine($"64-bit processo: {Environment.Is64BitProcess}");
        report.AppendLine($"Memoria fisica: {(memory is null ? "desconhecida" : $"{memory.TotalMb} MB total / {memory.AvailableMb} MB livre")}");
        report.AppendLine($"GameDir: {gameDir}");
        report.AppendLine($"Settings: {LauncherSettings.SettingsPath}");
        report.AppendLine($"AuthMode: {settings?.AuthMode ?? "desconhecido"}");
        report.AppendLine($"OfflineUsername: {settings?.OfflineUsername ?? "desconhecido"}");
        report.AppendLine($"MicrosoftUsername: {settings?.MicrosoftUsername ?? ""}");
        report.AppendLine($"RAM configurada: {settings?.MaximumRamMb.ToString() ?? "desconhecida"} MB");
        report.AppendLine($"Resolucao configurada: {(settings is null ? "desconhecida" : $"{settings.WindowWidth}x{settings.WindowHeight}")}");
        report.AppendLine($"Tela cheia: {settings?.FullScreen.ToString() ?? "desconhecido"}");
        report.AppendLine($"Visibilidade launcher: {settings?.LauncherVisibility ?? "desconhecida"}");
        report.AppendLine($"Modo compatibilidade: {settings?.CompatibilityMode.ToString() ?? "desconhecido"}");
        report.AppendLine($"Perfil performance: {settings?.PerformanceProfile ?? PerformanceProfiles.Auto}");
        report.AppendLine($"Perfil resourcepacks: {ResourcepackProfiles.ToDisplayName(settings?.ResourcepackProfile)}");
        report.AppendLine($"Java integrado: {settings?.UseIntegratedJava.ToString() ?? "desconhecido"}");
        report.AppendLine($"Java customizado: {settings?.JavaPath ?? ""}");
        report.AppendLine($"JVM extra: {settings?.ExtraJvmArguments ?? ""}");
        report.AppendLine($"ManifestUrl: {settings?.ManifestUrl ?? ProgramDefaults.ManifestUrl}");
        report.AppendLine($"Ultimo status: {lastStatus}");
        report.AppendLine();
    }

    private static async Task AppendKnownLogFilesAsync(StringBuilder report, string diagnosticsDir, string gameDir)
    {
        var latestCrashReport = FindNewestFile(Path.Combine(gameDir, "crash-reports"), "crash-*.txt");
        var latestJavaCrash = FindNewestJavaCrashLog(gameDir);
        var files = new[]
        {
            new ReportFile("Launcher UI log", Path.Combine(diagnosticsDir, "launcher-ui.log"), 180),
            new ReportFile("Launcher process log", Path.Combine(diagnosticsDir, "launcher.log"), 120),
            new ReportFile("Saida do Java", Path.Combine(diagnosticsDir, "minecraft-process.log"), 180),
            new ReportFile("Ultimo comando sanitizado", Path.Combine(diagnosticsDir, "last-launch-command.txt"), 40),
            new ReportFile("Telemetria local", TelemetryPath, 120),
            new ReportFile("Minecraft latest.log", Path.Combine(gameDir, "logs", "latest.log"), 220),
            new ReportFile("Minecraft crash report", latestCrashReport, 220),
            new ReportFile("Java crash log", latestJavaCrash, 220)
        };

        foreach (var file in files)
            await AppendFileTailAsync(report, file);
    }

    private static async Task AppendFileTailAsync(StringBuilder report, ReportFile file)
    {
        report.AppendLine($"===== {file.Title} =====");

        if (string.IsNullOrWhiteSpace(file.Path) || !File.Exists(file.Path))
        {
            report.AppendLine("Arquivo nao encontrado.");
            report.AppendLine();
            return;
        }

        report.AppendLine(file.Path);
        try
        {
            var lines = await File.ReadAllLinesAsync(file.Path, Encoding.UTF8);
            foreach (var line in lines.TakeLast(file.MaxLines))
                report.AppendLine(line);
        }
        catch (Exception ex)
        {
            report.AppendLine($"Nao foi possivel ler arquivo: {ex.Message}");
        }

        report.AppendLine();
    }

    private static void AppendSection(StringBuilder report, string title, string content)
    {
        report.AppendLine($"===== {title} =====");
        report.AppendLine(content);
        report.AppendLine();
    }

    private static string? FindNewestFile(string directory, string pattern)
    {
        try
        {
            if (!Directory.Exists(directory))
                return null;

            return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault()
                ?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private sealed record ReportFile(string Title, string? Path, int MaxLines);
}

internal sealed record CrashReportResult(string Path, string Text);

public sealed record SupportPackageResult(string ReportPath, string ZipPath, string Text);

public sealed record DiagnosticsSnapshot(DateTimeOffset CreatedAt, IReadOnlyList<DiagnosticItem> Items);

public sealed record DiagnosticItem(string Name, string Value, DiagnosticState State);

public enum DiagnosticState
{
    Ok,
    Warning,
    Error
}

internal enum PerformanceTier
{
    LowEnd,
    Balanced,
    Standard
}

internal readonly record struct GameWindowSize(int Width, int Height);

internal readonly record struct SystemMemorySnapshot(int TotalMb, int AvailableMb);

internal static class AuthModes
{
    public const string Microsoft = "microsoft";
    public const string Offline = "offline";
}

internal static class LauncherVisibilityModes
{
    public const string HideUntilGameExits = "hide";
    public const string MinimizeUntilGameExits = "minimize";
    public const string KeepOpen = "keep-open";
}

internal static class PerformanceProfiles
{
    public const string Auto = "auto";
    public const string Low = "low";
    public const string Balanced = "balanced";
    public const string High = "high";
}

internal static class ResourcepackProfiles
{
    public const string Full = "full";
    public const string Balanced = "balanced";
    public const string Essential = "essential";

    private static readonly string[] EssentialKeywords =
    [
        "icon",
        "minimap"
    ];

    private static readonly string[] BalancedBlockedKeywords =
    [
        "tcg",
        "animated",
        "fresh",
        "motion",
        "wallpaper",
        "costume",
        "overhaul",
        "shoulder",
        "decubes",
        "genomons",
        "mysticmons",
        "lost lore",
        "kale",
        "paradox",
        "dragon",
        "naruto",
        "pigeon",
        "oooooooo"
    ];

    public static string Normalize(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            Balanced => Balanced,
            Essential => Essential,
            _ => Full
        };
    }

    public static string ToDisplayName(string? value)
    {
        return Normalize(value) switch
        {
            Balanced => "Equilibrado",
            Essential => "Leve",
            _ => "Completo"
        };
    }

    public static bool Includes(string relativePath, string? profile)
    {
        var normalizedProfile = Normalize(profile);
        if (!relativePath.StartsWith("resourcepacks/", StringComparison.OrdinalIgnoreCase))
            return true;

        if (normalizedProfile == Full)
            return true;

        var fileName = Path.GetFileName(relativePath).ToLowerInvariant();
        if (normalizedProfile == Essential)
            return EssentialKeywords.Any(keyword => fileName.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        return !BalancedBlockedKeywords.Any(keyword => fileName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    public static int CountEnabledResourcepacks(ModpackManifest manifest, string? profile)
    {
        return manifest.Files.Count(file =>
            file.Path.StartsWith("resourcepacks/", StringComparison.OrdinalIgnoreCase)
            && Includes(file.Path.Replace('\\', '/'), profile));
    }
}

public sealed class LauncherSettings
{
    private const int LegacyRecommendedRamMb = 8192;
    private const int CurrentRuntimeDefaultsVersion = 6;

    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CobblemonLegacyLauncher",
        "launcher.settings.json");

    public string ManifestUrl { get; set; } = ProgramDefaults.ManifestUrl;
    public string GameDirectory { get; set; } = "%APPDATA%\\.cobblemonlegacy";
    public string OfflineUsername { get; set; } = "Player";
    public string AuthMode { get; set; } = "";
    public string MicrosoftUsername { get; set; } = "";
    public int MaximumRamMb { get; set; } = LauncherRuntime.GetRecommendedMaximumRamMb();
    public int WindowWidth { get; set; } = 854;
    public int WindowHeight { get; set; } = 480;
    public bool FullScreen { get; set; }
    public string LauncherVisibility { get; set; } = LauncherVisibilityModes.HideUntilGameExits;
    public bool CompatibilityMode { get; set; }
    public string PerformanceProfile { get; set; } = PerformanceProfiles.Auto;
    public string ResourcepackProfile { get; set; } = ResourcepackProfiles.Balanced;
    public bool UseIntegratedJava { get; set; } = true;
    public string JavaPath { get; set; } = "";
    public string ExtraJvmArguments { get; set; } = "";
    public int PerformancePresetVersion { get; set; }

    public LauncherSettings Clone()
    {
        return new LauncherSettings
        {
            ManifestUrl = ManifestUrl,
            GameDirectory = GameDirectory,
            OfflineUsername = OfflineUsername,
            AuthMode = AuthMode,
            MicrosoftUsername = MicrosoftUsername,
            MaximumRamMb = MaximumRamMb,
            WindowWidth = WindowWidth,
            WindowHeight = WindowHeight,
            FullScreen = FullScreen,
            LauncherVisibility = LauncherVisibility,
            CompatibilityMode = CompatibilityMode,
            PerformanceProfile = PerformanceProfile,
            ResourcepackProfile = ResourcepackProfile,
            UseIntegratedJava = UseIntegratedJava,
            JavaPath = JavaPath,
            ExtraJvmArguments = ExtraJvmArguments,
            PerformancePresetVersion = PerformancePresetVersion
        };
    }

    public void ApplyFrom(LauncherSettings source)
    {
        ManifestUrl = source.ManifestUrl;
        GameDirectory = source.GameDirectory;
        OfflineUsername = source.OfflineUsername;
        AuthMode = source.AuthMode;
        MicrosoftUsername = source.MicrosoftUsername;
        MaximumRamMb = source.MaximumRamMb;
        WindowWidth = source.WindowWidth;
        WindowHeight = source.WindowHeight;
        FullScreen = source.FullScreen;
        LauncherVisibility = source.LauncherVisibility;
        CompatibilityMode = source.CompatibilityMode;
        PerformanceProfile = source.PerformanceProfile;
        ResourcepackProfile = source.ResourcepackProfile;
        UseIntegratedJava = source.UseIntegratedJava;
        JavaPath = source.JavaPath;
        ExtraJvmArguments = source.ExtraJvmArguments;
        PerformancePresetVersion = source.PerformancePresetVersion;
    }

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

        var recommendedRamMb = LauncherRuntime.GetRecommendedMaximumRamMb();
        if (loaded.MaximumRamMb <= 0)
        {
            loaded.MaximumRamMb = recommendedRamMb;
            normalized = true;
        }

        if (loaded.MaximumRamMb == LegacyRecommendedRamMb && recommendedRamMb < LegacyRecommendedRamMb)
        {
            loaded.MaximumRamMb = recommendedRamMb;
            normalized = true;
        }

        loaded.OfflineUsername = string.IsNullOrWhiteSpace(loaded.OfflineUsername) ? "Player" : loaded.OfflineUsername.Trim();
        loaded.ManifestUrl = string.IsNullOrWhiteSpace(loaded.ManifestUrl) ? ProgramDefaults.ManifestUrl : loaded.ManifestUrl.Trim();
        if (ProgramDefaults.IsLegacyManifestUrl(loaded.ManifestUrl))
        {
            loaded.ManifestUrl = ProgramDefaults.ManifestUrl;
            normalized = true;
        }

        loaded.GameDirectory = string.IsNullOrWhiteSpace(loaded.GameDirectory) ? "%APPDATA%\\.cobblemonlegacy" : loaded.GameDirectory.Trim();
        loaded.AuthMode = NormalizeAuthMode(loaded.AuthMode);
        loaded.MicrosoftUsername = loaded.MicrosoftUsername?.Trim() ?? "";
        loaded.NormalizeRuntimeOptions();
        if (loaded.ApplyAutomaticHardwareDefaults(recommendedRamMb))
            normalized = true;

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
        if (ProgramDefaults.IsLegacyManifestUrl(ManifestUrl))
            ManifestUrl = ProgramDefaults.ManifestUrl;

        GameDirectory = string.IsNullOrWhiteSpace(GameDirectory) ? "%APPDATA%\\.cobblemonlegacy" : GameDirectory.Trim();
        MaximumRamMb = Math.Clamp(MaximumRamMb, 2048, 8192);
        NormalizeRuntimeOptions();

        await File.WriteAllTextAsync(SettingsPath, JsonSerializer.Serialize(this, jsonOptions), Encoding.UTF8);
    }

    private void NormalizeRuntimeOptions()
    {
        WindowWidth = Math.Clamp(WindowWidth <= 0 ? 854 : WindowWidth, 640, 3840);
        WindowHeight = Math.Clamp(WindowHeight <= 0 ? 480 : WindowHeight, 360, 2160);
        LauncherVisibility = NormalizeLauncherVisibility(LauncherVisibility);
        PerformanceProfile = NormalizePerformanceProfile(PerformanceProfile);
        ResourcepackProfile = ResourcepackProfiles.Normalize(ResourcepackProfile);
        JavaPath = JavaPath?.Trim() ?? "";
        ExtraJvmArguments = ExtraJvmArguments?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(JavaPath))
            UseIntegratedJava = true;
    }

    private bool ApplyAutomaticHardwareDefaults(int recommendedRamMb)
    {
        if (PerformancePresetVersion >= CurrentRuntimeDefaultsVersion)
            return false;

        var tier = LauncherRuntime.GetPerformanceTier();
        var windowSize = LauncherRuntime.GetRecommendedGameWindowSize();
        var changed = false;

        if (WindowWidth != windowSize.Width)
        {
            WindowWidth = windowSize.Width;
            changed = true;
        }

        if (WindowHeight != windowSize.Height)
        {
            WindowHeight = windowSize.Height;
            changed = true;
        }

        if (FullScreen && tier != PerformanceTier.Standard)
        {
            FullScreen = false;
            changed = true;
        }

        if (MaximumRamMb != recommendedRamMb && (MaximumRamMb <= 0 || MaximumRamMb == LegacyRecommendedRamMb || MaximumRamMb > recommendedRamMb))
        {
            MaximumRamMb = recommendedRamMb;
            changed = true;
        }

        if (tier == PerformanceTier.LowEnd)
        {
            if (string.Equals(PerformanceProfile, PerformanceProfiles.Auto, StringComparison.OrdinalIgnoreCase))
            {
                PerformanceProfile = PerformanceProfiles.Low;
                changed = true;
            }

            if (string.Equals(ResourcepackProfile, ResourcepackProfiles.Full, StringComparison.OrdinalIgnoreCase))
            {
                ResourcepackProfile = ResourcepackProfiles.Essential;
                changed = true;
            }

            if (!CompatibilityMode)
            {
                CompatibilityMode = true;
                changed = true;
            }
        }
        else
        {
            if (string.Equals(ResourcepackProfile, ResourcepackProfiles.Full, StringComparison.OrdinalIgnoreCase))
            {
                ResourcepackProfile = ResourcepackProfiles.Balanced;
                changed = true;
            }
        }

        if (changed)
            PerformancePresetVersion = 0;

        return changed;
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

    private static string NormalizeLauncherVisibility(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            LauncherVisibilityModes.HideUntilGameExits => LauncherVisibilityModes.HideUntilGameExits,
            LauncherVisibilityModes.MinimizeUntilGameExits => LauncherVisibilityModes.MinimizeUntilGameExits,
            LauncherVisibilityModes.KeepOpen => LauncherVisibilityModes.KeepOpen,
            _ => LauncherVisibilityModes.HideUntilGameExits
        };
    }

    private static string NormalizePerformanceProfile(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            PerformanceProfiles.Low => PerformanceProfiles.Low,
            PerformanceProfiles.Balanced => PerformanceProfiles.Balanced,
            PerformanceProfiles.High => PerformanceProfiles.High,
            _ => PerformanceProfiles.Auto
        };
    }
}

internal static class ProgramDefaults
{
    public const string ManifestUrl = "https://raw.githubusercontent.com/gotardelo/cobblemonlegacy-downloads/main/manifest.json";
    public const string NewsUrl = "https://raw.githubusercontent.com/gotardelo/cobblemonlegacy-downloads/main/news.json";
    public const string FabricMetaBaseUrl = "https://meta.fabricmc.net/v2";

    public static bool IsLegacyManifestUrl(string value)
    {
        return value.Contains("raw.githubusercontent.com/gotardelo/modpackscobblemonlegacy", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record PackReadiness(bool IsReady, string Message);

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
    private const string FabricKnotClientPath = "net/fabricmc/loader/impl/launch/knot/KnotClient.class";

    public static bool IsInstalled(string gameDir, string minecraftVersion, string loaderVersion)
    {
        return FindInstalledVersionId(gameDir, minecraftVersion, loaderVersion) is not null;
    }

    public static string? FindInstalledVersionId(string gameDir, string minecraftVersion, string loaderVersion)
    {
        if (string.Equals(loaderVersion, "latest", StringComparison.OrdinalIgnoreCase))
            return FindInstalledFabricVersion(gameDir, minecraftVersion)?.VersionId;

        var versionId = $"fabric-loader-{loaderVersion}-{minecraftVersion}";
        return File.Exists(GetVersionJsonPath(gameDir, versionId)) ? versionId : null;
    }

    public static async Task<bool> HasRequiredLibrariesAsync(
        string gameDir,
        string versionId,
        Action<string>? log = null)
    {
        var libraries = await ReadLibraryArtifactsAsync(gameDir, versionId);
        if (libraries.Count == 0)
        {
            log?.Invoke($"Fabric {versionId} sem bibliotecas no perfil.");
            return false;
        }

        foreach (var library in libraries)
        {
            if (!File.Exists(library.TargetPath))
            {
                log?.Invoke($"Biblioteca ausente: {library.RelativePath}");
                return false;
            }

            if (library.Size is not null && new FileInfo(library.TargetPath).Length != library.Size.Value)
            {
                log?.Invoke($"Biblioteca com tamanho invalido: {library.RelativePath}");
                return false;
            }
        }

        var fabricLoader = libraries.FirstOrDefault(library =>
            library.RelativePath.Replace('\\', '/').Contains("/fabric-loader/", StringComparison.OrdinalIgnoreCase));

        if (fabricLoader is null || !JarContainsEntry(fabricLoader.TargetPath, FabricKnotClientPath))
        {
            log?.Invoke("Biblioteca fabric-loader invalida: KnotClient nao encontrado.");
            return false;
        }

        return true;
    }

    public static async Task RepairLibrariesAsync(
        HttpClient http,
        string gameDir,
        string versionId,
        Action<string>? log = null)
    {
        var libraries = await ReadLibraryArtifactsAsync(gameDir, versionId);
        var repaired = 0;

        foreach (var library in libraries)
        {
            if (LibraryLooksValid(library))
                continue;

            if (string.IsNullOrWhiteSpace(library.Url))
                throw new InvalidOperationException($"Biblioteca sem URL para reparo: {library.RelativePath}");

            log?.Invoke($"Reparando biblioteca: {library.RelativePath}");
            Directory.CreateDirectory(Path.GetDirectoryName(library.TargetPath)!);
            var tempPath = $"{library.TargetPath}.download";

            try
            {
                using var response = await http.GetAsync(library.Url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                await using (var source = await response.Content.ReadAsStreamAsync())
                await using (var destination = File.Create(tempPath))
                {
                    await source.CopyToAsync(destination);
                }

                File.Move(tempPath, library.TargetPath, true);
                repaired++;
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        if (!await HasRequiredLibrariesAsync(gameDir, versionId, log))
            throw new InvalidOperationException("Nao foi possivel reparar as bibliotecas do Fabric. Tente novamente com a internet ativa.");

        log?.Invoke(repaired == 0
            ? "Bibliotecas do Fabric verificadas."
            : $"Bibliotecas do Fabric reparadas: {repaired} arquivo(s).");
    }

    public static async Task<string> InstallAsync(
        HttpClient http,
        string gameDir,
        string minecraftVersion,
        string loaderVersion,
        Action<string>? log = null)
    {
        var resolvedLoaderVersion = await ResolveLoaderVersionAsync(http, gameDir, minecraftVersion, loaderVersion, log);
        var expectedVersionId = $"fabric-loader-{resolvedLoaderVersion}-{minecraftVersion}";
        var expectedJsonPath = GetVersionJsonPath(gameDir, expectedVersionId);

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

    private static string GetVersionJsonPath(string gameDir, string versionId)
    {
        return Path.Combine(gameDir, "versions", versionId, $"{versionId}.json");
    }

    private static bool LibraryLooksValid(LibraryArtifact library)
    {
        if (!File.Exists(library.TargetPath))
            return false;

        if (library.Size is not null && new FileInfo(library.TargetPath).Length != library.Size.Value)
            return false;

        var normalizedPath = library.RelativePath.Replace('\\', '/');
        if (normalizedPath.Contains("/fabric-loader/", StringComparison.OrdinalIgnoreCase)
            && !JarContainsEntry(library.TargetPath, FabricKnotClientPath))
        {
            return false;
        }

        return true;
    }

    private static async Task<List<LibraryArtifact>> ReadLibraryArtifactsAsync(string gameDir, string versionId)
    {
        var jsonPath = GetVersionJsonPath(gameDir, versionId);
        if (!File.Exists(jsonPath))
            return [];

        await using var stream = File.OpenRead(jsonPath);
        using var document = await JsonDocument.ParseAsync(stream);
        if (!document.RootElement.TryGetProperty("libraries", out var librariesElement)
            || librariesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var librariesRoot = Path.Combine(gameDir, "libraries");
        var artifacts = new List<LibraryArtifact>();
        foreach (var library in librariesElement.EnumerateArray())
        {
            if (!library.TryGetProperty("downloads", out var downloads)
                || !downloads.TryGetProperty("artifact", out var artifact)
                || artifact.ValueKind != JsonValueKind.Object)
            {
                var legacyArtifact = TryCreateLegacyArtifact(gameDir, library);
                if (legacyArtifact is not null)
                    artifacts.Add(legacyArtifact);

                continue;
            }

            var relativePath = GetOptionalString(artifact, "path");
            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            artifacts.Add(new LibraryArtifact(
                relativePath.Replace('/', Path.DirectorySeparatorChar),
                Path.Combine(librariesRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                GetOptionalString(artifact, "url"),
                GetOptionalLong(artifact, "size")));
        }

        return artifacts;
    }

    private static LibraryArtifact? TryCreateLegacyArtifact(string gameDir, JsonElement library)
    {
        var name = GetOptionalString(library, "name");
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var parts = name.Split(':');
        if (parts.Length < 3)
            return null;

        var group = parts[0];
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length >= 4 ? parts[3] : "";
        var fileName = string.IsNullOrWhiteSpace(classifier)
            ? $"{artifact}-{version}.jar"
            : $"{artifact}-{version}-{classifier}.jar";
        var relativePath = Path.Combine(
            group.Split('.').Concat(new[] { artifact, version, fileName }).ToArray());
        var baseUrl = GetOptionalString(library, "url");
        var url = string.IsNullOrWhiteSpace(baseUrl)
            ? null
            : new Uri(new Uri(EnsureTrailingSlash(baseUrl)), relativePath.Replace('\\', '/')).ToString();

        return new LibraryArtifact(
            relativePath,
            Path.Combine(gameDir, "libraries", relativePath),
            url,
            GetOptionalLong(library, "size"));
    }

    private static string EnsureTrailingSlash(string value)
    {
        return value.EndsWith("/", StringComparison.Ordinal) ? value : $"{value}/";
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static long? GetOptionalLong(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;
    }

    private static bool JarContainsEntry(string path, string entryName)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return archive.GetEntry(entryName) is not null;
        }
        catch
        {
            return false;
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

    private sealed record LibraryArtifact(string RelativePath, string TargetPath, string? Url, long? Size);
}

internal static class ManagedFileSynchronizer
{
    public const string StateFileName = ".cobblemonlegacy-launcher-state.json";

    public static async Task<bool> IsSynchronizedAsync(
        string gameDir,
        ModpackManifest manifest,
        JsonSerializerOptions jsonOptions,
        string resourcepackProfile)
    {
        var statePath = Path.Combine(gameDir, StateFileName);
        var state = await LoadStateAsync(statePath, jsonOptions);
        if (!string.Equals(state.ManifestVersion, manifest.Version, StringComparison.OrdinalIgnoreCase))
            return false;

        var entries = GetEnabledEntries(manifest, resourcepackProfile);
        var expectedPaths = new HashSet<string>(entries.Select(entry => entry.RelativePath), StringComparer.OrdinalIgnoreCase);
        var managedPaths = new HashSet<string>(state.ManagedFiles, StringComparer.OrdinalIgnoreCase);

        if (managedPaths.Count != expectedPaths.Count || !expectedPaths.SetEquals(managedPaths))
            return false;

        foreach (var entry in entries)
        {
            var fullPath = ResolveGamePath(gameDir, entry.RelativePath);
            if (!File.Exists(fullPath))
                return false;

            if (entry.File.Size is not null && new FileInfo(fullPath).Length != entry.File.Size.Value)
                return false;
        }

        if (HasUnexpectedManagedFolderFiles(gameDir, "mods", expectedPaths))
            return false;

        if (HasUnexpectedManagedFolderFiles(gameDir, "resourcepacks", expectedPaths))
            return false;

        return true;
    }

    public static async Task SyncAsync(
        HttpClient http,
        string gameDir,
        ModpackManifest manifest,
        JsonSerializerOptions jsonOptions,
        string resourcepackProfile,
        Action<string>? log = null,
        Action<long, long>? byteProgress = null)
    {
        var statePath = Path.Combine(gameDir, StateFileName);
        var state = await LoadStateAsync(statePath, jsonOptions);
        var previousManagedFiles = new HashSet<string>(state.ManagedFiles, StringComparer.OrdinalIgnoreCase);
        var canTrustInstalledFiles = string.Equals(state.ManifestVersion, manifest.Version, StringComparison.OrdinalIgnoreCase);
        var entries = GetEnabledEntries(manifest, resourcepackProfile);
        var expectedPaths = new HashSet<string>(entries.Select(entry => entry.RelativePath), StringComparer.OrdinalIgnoreCase);
        var parallelDownloads = LauncherRuntime.GetRecommendedParallelDownloads();
        var semaphore = new SemaphoreSlim(parallelDownloads);
        var progressGate = new object();
        var nextProgressLog = DateTime.MinValue;
        var completed = 0;
        var reused = 0;
        var downloaded = 0;
        var skipped = 0;
        var totalPackBytes = entries.Sum(entry => Math.Max(0, entry.File.Size ?? 0));
        long progressedPackBytes = 0;
        var totalResourcepacks = manifest.Files.Count(file => file.Path.StartsWith("resourcepacks/", StringComparison.OrdinalIgnoreCase));
        var enabledResourcepacks = entries.Count(entry => entry.RelativePath.StartsWith("resourcepacks/", StringComparison.OrdinalIgnoreCase));
        log?.Invoke($"Resourcepacks: manifest completo ({enabledResourcepacks}/{totalResourcepacks}).");
        log?.Invoke($"Verificando pack com {parallelDownloads} download(s) paralelo(s).");

        void ReportPackProgress(long delta)
        {
            if (delta <= 0 || totalPackBytes <= 0)
                return;

            var current = Interlocked.Add(ref progressedPackBytes, delta);
            byteProgress?.Invoke(Math.Min(current, totalPackBytes), totalPackBytes);
        }

        void ReportRemainingEntryProgress(ManagedFile file, long entryProgress)
        {
            if (file.Size is not > 0)
                return;

            var remaining = file.Size.Value - entryProgress;
            if (remaining > 0)
                ReportPackProgress(remaining);
        }

        await Task.WhenAll(entries.Select(async entry =>
        {
            await semaphore.WaitAsync();
            try
            {
                long entryProgress = 0;
                Action<long, long>? entryByteProgress = totalPackBytes > 0
                    ? (current, _) =>
                    {
                        var safeCurrent = Math.Max(0, current);
                        var delta = safeCurrent - entryProgress;
                        if (delta <= 0)
                            return;

                        entryProgress = safeCurrent;
                        ReportPackProgress(delta);
                    }
                    : null;

                var result = await EnsureFileAsync(
                    http,
                    gameDir,
                    entry.RelativePath,
                    entry.File,
                    canTrustInstalledFiles && previousManagedFiles.Contains(entry.RelativePath),
                    log,
                    entryByteProgress);

                switch (result)
                {
                    case ManagedFileSyncResult.Reused:
                        ReportRemainingEntryProgress(entry.File, entryProgress);
                        Interlocked.Increment(ref reused);
                        break;
                    case ManagedFileSyncResult.Downloaded:
                        ReportRemainingEntryProgress(entry.File, entryProgress);
                        Interlocked.Increment(ref downloaded);
                        break;
                    case ManagedFileSyncResult.Skipped:
                        ReportRemainingEntryProgress(entry.File, entryProgress);
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

        removed += RemoveUnexpectedManagedFolderFiles(gameDir, "mods", expectedPaths, log);
        removed += RemoveUnexpectedManagedFolderFiles(gameDir, "resourcepacks", expectedPaths, log);

        state.ManifestVersion = manifest.Version;
        state.ManagedFiles = expectedPaths.Order(StringComparer.OrdinalIgnoreCase).ToList();
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state, jsonOptions), Encoding.UTF8);

        log?.Invoke($"Pack sincronizado: {reused} mantidos, {downloaded} baixados, {removed} removidos, {skipped} ignorados.");
    }

    private static ManagedFileEntry[] GetEnabledEntries(ModpackManifest manifest, string resourcepackProfile)
    {
        return manifest.Files
            .Select(file => new ManagedFileEntry(NormalizeRelativePath(file.Path), file))
            .Where(entry => ResourcepackProfiles.Includes(entry.RelativePath, resourcepackProfile))
            .ToArray();
    }

    private static int RemoveUnexpectedManagedFolderFiles(
        string gameDir,
        string folderName,
        HashSet<string> expectedPaths,
        Action<string>? log)
    {
        var folderPath = ResolveGamePath(gameDir, folderName);
        if (!Directory.Exists(folderPath))
            return 0;

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(file);
            if (fileName is ".gitkeep" or ".DS_Store" or "Thumbs.db")
                continue;

            var relativePath = NormalizeRelativePath($"{folderName}/{fileName}");
            if (expectedPaths.Contains(relativePath))
                continue;

            try
            {
                File.Delete(file);
                log?.Invoke($"Removido arquivo fora do manifest: {relativePath}");
                removed++;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Nao foi possivel remover arquivo antigo: {relativePath}. Feche o Minecraft e tente REPARAR. Detalhe: {ex.Message}", ex);
            }
        }

        return removed;
    }

    private static bool HasUnexpectedManagedFolderFiles(
        string gameDir,
        string folderName,
        HashSet<string> expectedPaths)
    {
        var folderPath = ResolveGamePath(gameDir, folderName);
        if (!Directory.Exists(folderPath))
            return false;

        foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(file);
            if (fileName is ".gitkeep" or ".DS_Store" or "Thumbs.db")
                continue;

            var relativePath = NormalizeRelativePath($"{folderName}/{fileName}");
            if (!expectedPaths.Contains(relativePath))
                return true;
        }

        return false;
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
