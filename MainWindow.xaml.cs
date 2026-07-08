using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;

namespace CobblemonLegacy;

public partial class MainWindow : Window
{
    private const int MaxVisibleLogCharacters = 12_000;
    private const long MaxUiLogBytes = 512 * 1024;
    private static readonly TimeSpan MinecraftStartupWatchTime = TimeSpan.FromSeconds(90);
    private static readonly Regex NicknameRegex = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);
    private static readonly string UiLogPath = Path.Combine(
        Path.GetDirectoryName(LauncherSettings.SettingsPath)!,
        "launcher-ui.log");

    private readonly HttpClient http;
    private readonly CancellationTokenSource serverStatusCancellation = new();
    private LauncherSettings? settings;
    private ModpackManifest? manifest;
    private MSession? microsoftSession;
    private LauncherUpdateInfo? availableUpdate;
    private LauncherNewsItem? currentNews;
    private LauncherNewsFeed? newsFeed;
    private Process? runningMinecraftProcess;
    private LauncherUpdateInfo? downloadedUpdate;
    private string? downloadedUpdateInstallerPath;
    private LauncherPrimaryAction primaryAction = LauncherPrimaryAction.Hidden;
    private bool isBusy;
    private bool isClosing;
    private ProcessPriorityClass? originalLauncherPriority;

    public MainWindow()
    {
        http = LauncherRuntime.CreateHttpClient();
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += (_, _) => isClosing = true;
        Closed += (_, _) =>
        {
            serverStatusCancellation.Cancel();
            serverStatusCancellation.Dispose();
            http.Dispose();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            AppendLog("Inicializando launcher...");

            settings = await LauncherSettings.LoadAsync(LauncherRuntime.JsonOptions);
            LauncherRuntime.WriteTelemetry("launcher_start", new Dictionary<string, object?>
            {
                ["authMode"] = settings.AuthMode,
                ["ramMb"] = settings.MaximumRamMb,
                ["performanceProfile"] = settings.PerformanceProfile,
                ["resourcepackProfile"] = settings.ResourcepackProfile
            });
            await EnsureAuthChoiceAsync();
            ApplySettingsToUi();

            SetStatus(HasPlayableProfile() ? "Verificando pack..." : "Escolha uma conta para liberar o JOGAR.");
            SetBusy(false);

            await LoadCachedManifestAsync();
            _ = LoadManifestInBackgroundAsync(serverStatusCancellation.Token);
            _ = CheckLauncherUpdateInBackgroundAsync(serverStatusCancellation.Token);
            _ = LoadNewsInBackgroundAsync(serverStatusCancellation.Token);
            _ = RefreshServerStatusLoopAsync(serverStatusCancellation.Token);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            ShowError(ex.Message);
        }
    }

    private async Task EnsureAuthChoiceAsync()
    {
        if (settings is null || !string.IsNullOrWhiteSpace(settings.AuthMode))
            return;

        var dialog = new LoginChoiceWindow(settings.OfflineUsername)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
            return;

        if (dialog.SelectedAuthMode == AuthModes.Microsoft)
        {
            try
            {
                await SignInWithMicrosoftAsync();
            }
            catch (Exception ex) when (IsMicrosoftAuthCancellation(ex))
            {
                SetStatus("Login Microsoft cancelado. Escolha uma conta para liberar o JOGAR.");
            }
        }
        else
        {
            settings.AuthMode = dialog.SelectedAuthMode;
            if (settings.AuthMode == AuthModes.Offline)
                settings.OfflineUsername = dialog.OfflineNickname;

            await settings.SaveAsync(LauncherRuntime.JsonOptions);
        }
    }

    private async Task LoadCachedManifestAsync()
    {
        var cachedManifest = await ModpackManifestLoader.TryLoadCachedAsync(LauncherRuntime.JsonOptions);
        if (cachedManifest is null)
            return;

        manifest = cachedManifest;
        ApplyManifestToUi(cachedManifest);
        await RefreshPrimaryActionAsync();
        AppendLog("Manifest em cache carregado.");
    }

    private async Task LoadManifestInBackgroundAsync(CancellationToken cancellationToken)
    {
        if (settings is null)
            return;

        try
        {
            var loadedManifest = await ModpackManifestLoader.LoadAsync(http, settings.ManifestUrl, LauncherRuntime.JsonOptions, AppendLog);
            if (cancellationToken.IsCancellationRequested)
                return;

            manifest = loadedManifest;
            Dispatcher.Invoke(() => ApplyManifestToUi(loadedManifest));
            await RefreshPrimaryActionAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"Manifest remoto indisponivel: {ex.Message}");
            if (manifest is null)
            {
                SetPrimaryAction(HasPlayableProfile() ? LauncherPrimaryAction.NeedsUpdate : LauncherPrimaryAction.Hidden);
                SetStatus("Nao foi possivel carregar o manifest. Tente novamente em instantes.");
            }
        }
    }

    private void ApplyManifestToUi(ModpackManifest loadedManifest)
    {
        var mods = loadedManifest.Files.Count(file => file.Path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase));
        var resourcepacks = loadedManifest.Files.Count(file => file.Path.StartsWith("resourcepacks/", StringComparison.OrdinalIgnoreCase));
        var enabledResourcepacks = settings is null
            ? resourcepacks
            : ResourcepackProfiles.CountEnabledResourcepacks(loadedManifest, settings.ResourcepackProfile);

        ManifestText.Text = $"v{loadedManifest.Version}";
        PackSummaryText.Text = $"{mods} mods | {enabledResourcepacks}/{resourcepacks} resourcepacks";
        IntegrityText.Text = $"Integridade: {loadedManifest.Files.Count} arquivos no manifest.";
        AppendLog($"Manifest carregado: {mods} mods, {resourcepacks} resourcepacks.");
        LauncherRuntime.WriteTelemetry("manifest_loaded", new Dictionary<string, object?>
        {
            ["version"] = loadedManifest.Version,
            ["mods"] = mods,
            ["resourcepacks"] = resourcepacks,
            ["enabledResourcepacks"] = enabledResourcepacks
        });
    }

    private async Task<ModpackManifest> EnsureManifestForPlayAsync()
    {
        if (manifest is not null)
            return manifest;

        if (settings is null)
            throw new InvalidOperationException("Configuracao nao carregada.");

        var loadedManifest = await ModpackManifestLoader.LoadAsync(http, settings.ManifestUrl, LauncherRuntime.JsonOptions, AppendLog);
        manifest = loadedManifest;
        ApplyManifestToUi(loadedManifest);
        return loadedManifest;
    }

    private void ApplySettingsToUi()
    {
        if (settings is null)
            return;

        NicknameTextBox.Text = settings.OfflineUsername;
        ShowNicknameEditor(false);
        SetPrimaryAction(HasPlayableProfile() ? LauncherPrimaryAction.Checking : LauncherPrimaryAction.Hidden);
        RamText.Text = $"RAM: {settings.MaximumRamMb} MB | RP: {ResourcepackProfiles.ToDisplayName(settings.ResourcepackProfile)}";
        UpdateAccountText();
    }

    private void UpdateAccountText()
    {
        if (settings is null)
            return;

        var hasProfile = !string.IsNullOrWhiteSpace(settings.AuthMode);
        LoginTitleText.Text = hasProfile ? "Perfil Cobblemon Legacy" : "Opcoes de Login";
        AccountText.Visibility = hasProfile ? Visibility.Visible : Visibility.Collapsed;
        AccountText.Text = settings.AuthMode switch
        {
            AuthModes.Microsoft when !string.IsNullOrWhiteSpace(settings.MicrosoftUsername) => $"Perfil Microsoft: {settings.MicrosoftUsername}",
            AuthModes.Microsoft => "Conecte sua conta Microsoft",
            AuthModes.Offline => $"Perfil offline: {settings.OfflineUsername}",
            _ => "Nenhuma conta selecionada"
        };
    }

    private bool HasPlayableProfile()
    {
        if (settings is null)
            return false;

        return settings.AuthMode switch
        {
            AuthModes.Microsoft => !string.IsNullOrWhiteSpace(settings.MicrosoftUsername),
            AuthModes.Offline => NicknameRegex.IsMatch(settings.OfflineUsername),
            _ => false
        };
    }

    private async Task RefreshPrimaryActionAsync()
    {
        if (!HasPlayableProfile())
        {
            SetPrimaryAction(LauncherPrimaryAction.Hidden);
            return;
        }

        if (settings is null || manifest is null)
        {
            SetPrimaryAction(LauncherPrimaryAction.Checking);
            return;
        }

        try
        {
            SetPrimaryAction(LauncherPrimaryAction.Checking);
            var gameDir = LauncherRuntime.ExpandGameDirectory(settings);
            var readiness = await LauncherRuntime.CheckPackReadinessAsync(gameDir, manifest, LauncherRuntime.JsonOptions, settings.ResourcepackProfile);
            SetPrimaryAction(readiness.IsReady ? LauncherPrimaryAction.Ready : LauncherPrimaryAction.NeedsUpdate);
            SetIntegrityStatus(readiness.IsReady
                ? $"Integridade: {manifest.Files.Count} arquivos verificados."
                : $"Integridade: {readiness.Message}");
            SetStatus(readiness.Message);
        }
        catch (Exception ex)
        {
            AppendLog($"Nao foi possivel verificar o pack: {ex.Message}");
            SetPrimaryAction(LauncherPrimaryAction.NeedsUpdate);
            SetIntegrityStatus("Integridade: verificacao pendente.");
            SetStatus("Nao foi possivel verificar o pack. Clique em ATUALIZAR.");
        }
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        switch (primaryAction)
        {
            case LauncherPrimaryAction.NeedsUpdate:
                await UpdatePackOnlyAsync();
                break;
            case LauncherPrimaryAction.Ready:
                await PlayAsync();
                break;
            default:
                SetStatus(isBusy
                    ? "Aguarde a operacao atual terminar."
                    : "O botao JOGAR ainda nao esta liberado. Escolha uma conta e aguarde a verificacao do pack.");
                break;
        }
    }

    private async Task PlayAsync()
    {
        if (isBusy || settings is null)
            return;

        var launchStopwatch = Stopwatch.StartNew();
        Process? process = null;
        try
        {
            SetBusy(true);
            SetPrimaryAction(LauncherPrimaryAction.Updating);
            ProgressBar.IsIndeterminate = true;
            LauncherRuntime.WriteTelemetry("minecraft_launch_requested", new Dictionary<string, object?>
            {
                ["authMode"] = settings.AuthMode,
                ["ramMb"] = settings.MaximumRamMb,
                ["performanceProfile"] = settings.PerformanceProfile,
                ["resourcepackProfile"] = settings.ResourcepackProfile
            });

            await SaveOfflineNicknameIfNeededAsync();
            if (isClosing)
                return;

            var session = await ResolveSessionAsync();
            if (isClosing)
                return;

            var activeManifest = await EnsureManifestForPlayAsync();
            var gameDir = LauncherRuntime.ExpandGameDirectory(settings);
            var readiness = await RunPreflightAsync(activeManifest, gameDir);
            var versionId = await UpdatePackCoreAsync(launchAfterUpdate: true, activeManifest, readiness);
            if (isClosing)
                return;

            var launcher = LauncherRuntime.CreateMinecraftLauncher(gameDir, SetStatus, SetByteProgress);

            SetPrimaryAction(LauncherPrimaryAction.Launching);
            AppendPreflightLine("Minecraft: iniciando...");
            SetStatus("Abrindo Minecraft...");
            process = await LauncherRuntime.StartGameAsync(launcher, versionId, settings, session, AppendLog);
            runningMinecraftProcess = process;
            AppendLog($"Minecraft iniciado com PID {process.Id}.");
            var windowDetected = await WaitForMinecraftStartupAsync(process, gameDir);

            if (windowDetected)
            {
                SetStatus(GetMinecraftRunningStatus());
                await MonitorLauncherDuringGameAsync(process);
            }
            else
            {
                SetStatus("Minecraft ainda esta iniciando. Launcher ficara aberto ate a janela aparecer ou o jogo fechar.");
                await WaitForGameExitWithLauncherOpenAsync(process);
            }
            LauncherRuntime.WriteTelemetry("minecraft_closed", new Dictionary<string, object?>
            {
                ["exitCode"] = TryGetExitCode(process),
                ["seconds"] = Math.Round(launchStopwatch.Elapsed.TotalSeconds, 1)
            });
            SetPrimaryAction(LauncherPrimaryAction.Ready);
        }
        catch (Exception ex) when (IsMicrosoftAuthCancellation(ex))
        {
            SetPrimaryAction(HasPlayableProfile() ? LauncherPrimaryAction.Ready : LauncherPrimaryAction.Hidden);
            SetStatus("Login Microsoft cancelado.");
        }
        catch (Exception ex)
        {
            LauncherRuntime.WriteTelemetry("minecraft_launch_failed", new Dictionary<string, object?>
            {
                ["error"] = ex.Message,
                ["exitCode"] = process is null ? "" : TryGetExitCode(process),
                ["seconds"] = Math.Round(launchStopwatch.Elapsed.TotalSeconds, 1)
            });
            _ = RefreshPrimaryActionAsync();
            ShowError(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(runningMinecraftProcess, process))
                runningMinecraftProcess = null;

            SetBusy(false);
        }
    }

    private async Task<bool> WaitForMinecraftStartupAsync(Process process, string gameDir)
    {
        SetStatus("Minecraft iniciando... aguarde a janela abrir.");

        var deadline = DateTime.UtcNow + MinecraftStartupWatchTime;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                break;

            process.Refresh();
            if (LauncherRuntime.HasProcessWindow(process))
                return true;

            await Task.Delay(1000);
        }

        if (!process.HasExited)
        {
            AppendLog("Minecraft continuou rodando sem janela detectada dentro do tempo de seguranca.");
            return false;
        }

        var exitCode = TryGetExitCode(process);
        var logHint = await LauncherRuntime.GetLatestMinecraftLogHintAsync(gameDir);
        throw new InvalidOperationException(
            $"O Minecraft fechou antes de abrir corretamente (codigo {exitCode}).{Environment.NewLine}" +
            $"{logHint}{Environment.NewLine}" +
            "O launcher ficou aberto para voce tentar novamente ou enviar esse erro para o suporte.");
    }

    private async Task<PackReadiness> RunPreflightAsync(ModpackManifest activeManifest, string gameDir)
    {
        if (settings is null)
            throw new InvalidOperationException("Configuracao nao carregada.");

        SetStatus("Pre-voo: conferindo perfil, memoria, pack e servidor...");
        ShowPreflightPanel();
        UpdatePreflight("Perfil: verificando...\nMemoria: aguardando\nPack: aguardando\nServidor: aguardando");

        var profileLine = settings.AuthMode switch
        {
            AuthModes.Microsoft => string.IsNullOrWhiteSpace(settings.MicrosoftUsername)
                ? "Perfil: Microsoft precisa de login"
                : $"Perfil: Microsoft ({settings.MicrosoftUsername})",
            AuthModes.Offline => $"Perfil: Offline ({settings.OfflineUsername})",
            _ => "Perfil: escolha uma conta"
        };
        UpdatePreflight($"{profileLine}\nMemoria: verificando...\nPack: aguardando\nServidor: aguardando");

        var memoryLine = BuildMemoryPreflightLine();
        UpdatePreflight($"{profileLine}\n{memoryLine}\nPack: verificando...\nServidor: aguardando");

        var readiness = await LauncherRuntime.CheckPackReadinessAsync(gameDir, activeManifest, LauncherRuntime.JsonOptions, settings.ResourcepackProfile);
        var packLine = readiness.IsReady
            ? $"Pack: OK ({activeManifest.Files.Count} arquivos)"
            : $"Pack: precisa atualizar ({readiness.Message})";
        UpdatePreflight($"{profileLine}\n{memoryLine}\n{packLine}\nServidor: consultando...");

        var serverLine = await BuildServerPreflightLineAsync();
        UpdatePreflight($"{profileLine}\n{memoryLine}\n{packLine}\n{serverLine}");
        return readiness;
    }

    private string BuildMemoryPreflightLine()
    {
        var memory = LauncherRuntime.GetSystemMemorySnapshot();
        if (memory is null || settings is null)
            return "Memoria: OK";

        var safeFree = Math.Max(0, memory.Value.AvailableMb);
        if (safeFree < 2_500)
            return $"Memoria: atencao ({safeFree} MB livres, launcher ajusta automaticamente)";

        return $"Memoria: OK ({safeFree} MB livres)";
    }

    private async Task<string> BuildServerPreflightLineAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var status = await MinecraftServerStatusClient.QueryAsync(LauncherRuntime.ServerIp, timeout.Token);
            return status.IsAvailable
                ? $"Servidor: OK ({status.Online}/{status.Max}, {status.LatencyMs}ms)"
                : "Servidor: indisponivel no momento";
        }
        catch
        {
            return "Servidor: sem resposta rapida, tentando abrir mesmo assim";
        }
    }

    private void ShowPreflightPanel()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ShowPreflightPanel);
            return;
        }

        PreflightPanel.Visibility = Visibility.Visible;
    }

    private void UpdatePreflight(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdatePreflight(message));
            return;
        }

        PreflightText.Text = message;
    }

    private void AppendPreflightLine(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendPreflightLine(message));
            return;
        }

        PreflightText.Text = $"{PreflightText.Text.TrimEnd()}{Environment.NewLine}{message}";
    }

    private static string TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode.ToString();
        }
        catch
        {
            return "desconhecido";
        }
    }

    private string GetMinecraftRunningStatus()
    {
        return settings?.LauncherVisibility switch
        {
            LauncherVisibilityModes.KeepOpen => "Minecraft aberto. O launcher ficara aberto.",
            LauncherVisibilityModes.MinimizeUntilGameExits => "Minecraft aberto. Launcher minimizado ate o jogo fechar.",
            _ => "Minecraft aberto. O launcher volta quando o jogo fechar."
        };
    }

    private async Task MonitorLauncherDuringGameAsync(Process process)
    {
        switch (settings?.LauncherVisibility)
        {
            case LauncherVisibilityModes.KeepOpen:
                await WaitForGameExitWithLauncherOpenAsync(process);
                break;
            case LauncherVisibilityModes.MinimizeUntilGameExits:
                await MinimizeLauncherUntilGameExitsAsync(process);
                break;
            default:
                await HideLauncherUntilGameExitsAsync(process);
                break;
        }
    }

    private async Task WaitForGameExitWithLauncherOpenAsync(Process process)
    {
        if (!process.HasExited)
            await process.WaitForExitAsync();

        if (!isClosing)
            SetStatus("Minecraft fechado. Launcher pronto.");
    }

    private async Task MinimizeLauncherUntilGameExitsAsync(Process process)
    {
        var previousState = WindowState;

        try
        {
            EnterLauncherBackgroundMode("Launcher minimizado em segundo plano ate o Minecraft fechar.");
            WindowState = WindowState.Minimized;

            if (!process.HasExited)
                await process.WaitForExitAsync();
        }
        finally
        {
            if (!isClosing)
            {
                ExitLauncherBackgroundMode();
                WindowState = previousState == WindowState.Minimized ? WindowState.Normal : previousState;
                Activate();
                SetStatus("Minecraft fechado. Launcher reaberto.");
            }
        }
    }

    private async Task HideLauncherUntilGameExitsAsync(Process process)
    {
        var showInTaskbar = ShowInTaskbar;

        try
        {
            EnterLauncherBackgroundMode("Launcher escondido em segundo plano ate o Minecraft fechar.");
            ShowInTaskbar = false;
            Hide();

            if (!process.HasExited)
                await process.WaitForExitAsync();
        }
        finally
        {
            if (!isClosing)
            {
                ExitLauncherBackgroundMode();
                ShowInTaskbar = showInTaskbar;
                Show();
                WindowState = WindowState.Normal;
                Activate();
                SetStatus("Minecraft fechado. Launcher reaberto.");
            }
        }
    }

    private void EnterLauncherBackgroundMode(string message)
    {
        AppendLog(message);
        SetStatus(message);

        try
        {
            var currentProcess = Process.GetCurrentProcess();
            originalLauncherPriority ??= currentProcess.PriorityClass;
            currentProcess.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex)
        {
            AppendLog($"Nao foi possivel reduzir prioridade do launcher: {ex.Message}");
        }

        LauncherRuntime.TrimCurrentProcessMemory();
    }

    private void ExitLauncherBackgroundMode()
    {
        try
        {
            if (originalLauncherPriority is not null)
            {
                Process.GetCurrentProcess().PriorityClass = originalLauncherPriority.Value;
                originalLauncherPriority = null;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Nao foi possivel restaurar prioridade do launcher: {ex.Message}");
        }
    }

    private async Task UpdatePackOnlyAsync()
    {
        if (isBusy || settings is null)
            return;

        try
        {
            SetBusy(true);
            SetPrimaryAction(LauncherPrimaryAction.Updating);
            ProgressBar.IsIndeterminate = true;
            await SaveOfflineNicknameIfNeededAsync();
            await UpdatePackCoreAsync(launchAfterUpdate: false);
            SetPrimaryAction(LauncherPrimaryAction.Ready);
            SetStatus("Pack atualizado. Clique em JOGAR.");
        }
        catch (Exception ex)
        {
            _ = RefreshPrimaryActionAsync();
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<string> UpdatePackCoreAsync(
        bool launchAfterUpdate,
        ModpackManifest? knownManifest = null,
        PackReadiness? knownReadiness = null)
    {
        if (settings is null)
            throw new InvalidOperationException("Configuracao nao carregada.");

        var stopwatch = Stopwatch.StartNew();
        var activeManifest = knownManifest ?? await EnsureManifestForPlayAsync();
        LauncherRuntime.WriteTelemetry("pack_update_start", new Dictionary<string, object?>
        {
            ["manifestVersion"] = activeManifest.Version,
            ["resourcepackProfile"] = settings.ResourcepackProfile,
            ["launchAfterUpdate"] = launchAfterUpdate
        });

        var gameDir = LauncherRuntime.ExpandGameDirectory(settings);
        if (launchAfterUpdate)
        {
            SetStatus("Conferindo pack...");
            var readiness = knownReadiness ?? await LauncherRuntime.CheckPackReadinessAsync(gameDir, activeManifest, LauncherRuntime.JsonOptions, settings.ResourcepackProfile);
            if (readiness.IsReady)
            {
                var installedVersionId = FabricProfileInstaller.FindInstalledVersionId(
                    gameDir,
                    activeManifest.MinecraftVersion,
                    activeManifest.FabricLoaderVersion);

                if (!string.IsNullOrWhiteSpace(installedVersionId))
                {
                    await MinecraftProfileConfigurator.ConfigureAsync(gameDir, settings, SetStatus);
                    ProgressBar.IsIndeterminate = false;
                    ProgressBar.Value = 100;
                    ProgressPercentText.Text = "100%";
                    SetIntegrityStatus($"Integridade: {activeManifest.Files.Count} arquivos verificados.");
                    SetStatus("Pack pronto. Abrindo jogo...");
                    LauncherRuntime.WriteTelemetry("pack_update_skipped_ready", new Dictionary<string, object?>
                    {
                        ["manifestVersion"] = activeManifest.Version,
                        ["seconds"] = Math.Round(stopwatch.Elapsed.TotalSeconds, 1)
                    });

                    return installedVersionId;
                }
            }

            SetStatus($"Atualizando pack: {readiness.Message}");
        }

        var launcher = LauncherRuntime.CreateMinecraftLauncher(gameDir, SetStatus, SetByteProgress);

        var versionId = await LauncherRuntime.InstallOrUpdateAsync(http, launcher, activeManifest, gameDir, settings.ResourcepackProfile, SetStatus, SetByteProgress);
        await MinecraftProfileConfigurator.ConfigureAsync(gameDir, settings, SetStatus);

        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = 100;
        SetStatus(launchAfterUpdate ? "Pack atualizado. Preparando jogo..." : "Pack atualizado.");
        LauncherRuntime.WriteTelemetry("pack_update_end", new Dictionary<string, object?>
        {
            ["manifestVersion"] = activeManifest.Version,
            ["seconds"] = Math.Round(stopwatch.Elapsed.TotalSeconds, 1)
        });

        return versionId;
    }

    private async Task RepairInstallationAsync()
    {
        if (isBusy)
        {
            SetStatus("Aguarde a operacao atual terminar antes de reparar.");
            return;
        }

        if (settings is null)
        {
            SetStatus("Configuracao ainda nao carregada.");
            return;
        }

        var result = MessageBox.Show(
            this,
            "O reparo vai verificar o pack inteiro e reinstalar o Fabric. Mundos e saves serao preservados. Continuar?",
            "Cobblemon Legacy",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            SetBusy(true);
            SetPrimaryAction(LauncherPrimaryAction.Updating);
            ProgressBar.IsIndeterminate = true;
            SetIntegrityStatus("Integridade: reparo em andamento...");

            var activeManifest = await EnsureManifestForPlayAsync();
            var gameDir = LauncherRuntime.ExpandGameDirectory(settings);
            var launcher = LauncherRuntime.CreateMinecraftLauncher(gameDir, SetStatus, SetByteProgress);

            LauncherRuntime.WriteTelemetry("repair_start", new Dictionary<string, object?>
            {
                ["manifestVersion"] = activeManifest.Version,
                ["resourcepackProfile"] = settings.ResourcepackProfile
            });

            await LauncherRuntime.RepairInstallationAsync(http, launcher, activeManifest, gameDir, settings.ResourcepackProfile, SetStatus, SetByteProgress);
            await MinecraftProfileConfigurator.ConfigureAsync(gameDir, settings, SetStatus);

            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 100;
            SetPrimaryAction(LauncherPrimaryAction.Ready);
            SetIntegrityStatus($"Integridade: reparo concluido com {activeManifest.Files.Count} arquivos.");
            SetStatus("Reparo concluido. Pode clicar em JOGAR.");
            LauncherRuntime.WriteTelemetry("repair_end", new Dictionary<string, object?>
            {
                ["manifestVersion"] = activeManifest.Version
            });
        }
        catch (Exception ex)
        {
            _ = RefreshPrimaryActionAsync();
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<MSession> ResolveSessionAsync()
    {
        if (settings is null)
            throw new InvalidOperationException("Configuracao nao carregada.");

        if (settings.AuthMode == AuthModes.Microsoft)
        {
            if (microsoftSession is not null)
                return microsoftSession;

            return await SignInWithMicrosoftAsync();
        }

        if (!NicknameRegex.IsMatch(settings.OfflineUsername))
        {
            ShowNicknameEditor(true);
            throw new InvalidOperationException("Defina um nickname valido antes de jogar.");
        }

        var offlineSession = MSession.CreateOfflineSession(settings.OfflineUsername);
        offlineSession.UserType = "legacy";
        offlineSession.AccessToken = "0";
        offlineSession.ClientToken = "";
        offlineSession.Xuid = "";
        return offlineSession;
    }

    private async Task SaveOfflineNicknameIfNeededAsync()
    {
        if (settings is null || settings.AuthMode != AuthModes.Offline)
            return;

        var nickname = NicknameTextBox.Text.Trim();
        if (!NicknameRegex.IsMatch(nickname))
            throw new InvalidOperationException("Nickname invalido. Use 3 a 16 caracteres: letras, numeros ou underline.");

        settings.OfflineUsername = nickname;
        settings.MicrosoftUsername = "";
        microsoftSession = null;
        await settings.SaveAsync(LauncherRuntime.JsonOptions);
        UpdateAccountText();
        if (!isBusy)
            await RefreshPrimaryActionAsync();
    }

    private async void SaveNicknameButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (settings is null)
                return;

            settings.AuthMode = AuthModes.Offline;
            await SaveOfflineNicknameIfNeededAsync();
            ShowNicknameEditor(false);
            SetStatus($"Perfil offline salvo: {settings.OfflineUsername}.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void MicrosoftModeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (settings is null)
                return;

            if (isBusy)
            {
                SetStatus("Aguarde a operacao atual terminar antes de trocar a conta.");
                return;
            }

            SetBusy(true);
            ProgressBar.IsIndeterminate = true;
            ShowNicknameEditor(false);
            await SignInWithMicrosoftAsync();
            await RefreshPrimaryActionAsync();
            SetStatus($"Conta Microsoft conectada: {settings.MicrosoftUsername}. Pode clicar em JOGAR.");
        }
        catch (Exception ex) when (IsMicrosoftAuthCancellation(ex))
        {
            SetPrimaryAction(HasPlayableProfile() ? LauncherPrimaryAction.Checking : LauncherPrimaryAction.Hidden);
            _ = RefreshPrimaryActionAsync();
            SetStatus("Login Microsoft cancelado.");
        }
        catch (Exception ex)
        {
            _ = RefreshPrimaryActionAsync();
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OfflineModeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (settings is null)
                return;

            NicknameTextBox.Text = settings.OfflineUsername;
            ShowNicknameEditor(true);
            SetPrimaryAction(LauncherPrimaryAction.Hidden);
            SetStatus("Digite seu nickname e clique em Salvar.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void CopyIpButton_Click(object sender, RoutedEventArgs e)
    {
        TryCopyToClipboard(LauncherRuntime.ServerIp, "IP copiado.");
    }

    private async Task<MSession> SignInWithMicrosoftAsync()
    {
        if (settings is null)
            throw new InvalidOperationException("Configuracao nao carregada.");

        ShowNicknameEditor(false);
        SetStatus("Abrindo login Microsoft...");
        AppendLog("Login Microsoft iniciado.");

        var loginHandler = JELoginHandlerBuilder.BuildDefault();
        var session = await loginHandler.Authenticate();

        microsoftSession = session;
        settings.AuthMode = AuthModes.Microsoft;
        settings.MicrosoftUsername = string.IsNullOrWhiteSpace(session.Username) ? "Conta Microsoft" : session.Username;
        await settings.SaveAsync(LauncherRuntime.JsonOptions);

        UpdateAccountText();
        if (!isBusy)
            await RefreshPrimaryActionAsync();

        return session;
    }

    private static bool IsMicrosoftAuthCancellation(Exception ex)
    {
        if (ex is OperationCanceledException)
            return true;

        if (ex is AggregateException aggregateException)
            return aggregateException.Flatten().InnerExceptions.Any(IsMicrosoftAuthCancellation);

        var message = ex.Message;
        return message.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase)
            || message.Contains("operation was cancelled", StringComparison.OrdinalIgnoreCase)
            || message.Contains("authentication canceled", StringComparison.OrdinalIgnoreCase)
            || message.Contains("authentication cancelled", StringComparison.OrdinalIgnoreCase);
    }

    private void DiscordButton_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalUrl("https://discord.gg/sETS2Fc7Ey");
    }

    private void WebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalUrl("https://www.cobblemonlegacy.com.br");
    }

    private async void CrashReportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = await LauncherRuntime.CreateSupportPackageAsync(
                settings,
                LogTextBox.Text,
                StatusText.Text);

            TryCopyToClipboard(package.Text, "Relatorio copiado.");
            OpenFileLocation(package.ZipPath);
            OpenExternalUrl("https://discord.gg/sETS2Fc7Ey");
            SetStatus("Pacote ZIP criado, relatorio copiado e Discord aberto para suporte.");
        }
        catch (Exception ex)
        {
            ShowError($"Nao foi possivel gerar o relatorio: {ex.Message}");
        }
    }

    private async void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var snapshot = await LauncherRuntime.CreateDiagnosticsSnapshotAsync(settings, manifest, StatusText.Text);
            var dialog = new DiagnosticsWindow(
                snapshot,
                () => LauncherRuntime.CreateSupportPackageAsync(settings, LogTextBox.Text, StatusText.Text))
            {
                Owner = this
            };

            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            ShowError($"Nao foi possivel abrir o diagnostico: {ex.Message}");
        }
    }

    private async void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        await RepairInstallationAsync();
    }

    private async void VerifyButton_Click(object sender, RoutedEventArgs e)
    {
        if (isBusy)
        {
            SetStatus("Aguarde a operacao atual terminar antes de verificar.");
            return;
        }

        if (settings is null)
        {
            SetStatus("Configuracao ainda nao carregada.");
            return;
        }

        try
        {
            SetBusy(true);
            SetPrimaryAction(LauncherPrimaryAction.Checking);
            SetStatus("Verificando integridade do pack...");
            var activeManifest = await EnsureManifestForPlayAsync();
            var gameDir = LauncherRuntime.ExpandGameDirectory(settings);
            var readiness = await LauncherRuntime.CheckPackReadinessAsync(gameDir, activeManifest, LauncherRuntime.JsonOptions, settings.ResourcepackProfile);
            SetPrimaryAction(readiness.IsReady ? LauncherPrimaryAction.Ready : LauncherPrimaryAction.NeedsUpdate);
            SetIntegrityStatus(readiness.IsReady
                ? $"Integridade: ok ({ResourcepackProfiles.ToDisplayName(settings.ResourcepackProfile)})."
                : $"Integridade: {readiness.Message}");
            SetStatus(readiness.Message);
            LauncherRuntime.WriteTelemetry("pack_verify", new Dictionary<string, object?>
            {
                ["ready"] = readiness.IsReady,
                ["message"] = readiness.Message,
                ["resourcepackProfile"] = settings.ResourcepackProfile
            });
        }
        catch (Exception ex)
        {
            _ = RefreshPrimaryActionAsync();
            ShowError($"Nao foi possivel verificar o pack: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OptionsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (isBusy)
            {
                SetStatus("Aguarde a operacao atual terminar antes de abrir as opcoes.");
                return;
            }

            if (settings is null)
            {
                SetStatus("Configuracao ainda nao carregada.");
                return;
            }

            var dialog = new OptionsWindow(settings)
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
                return;

            settings.ApplyFrom(dialog.Settings);
            await settings.SaveAsync(LauncherRuntime.JsonOptions);
            ApplySettingsToUi();
            if (manifest is not null)
                ApplyManifestToUi(manifest);
            await RefreshPrimaryActionAsync();
            SetStatus("Opcoes salvas.");
        }
        catch (Exception ex)
        {
            ShowError($"Nao foi possivel salvar as opcoes: {ex.Message}");
        }
    }

    private void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
            SetStatus("Link aberto no navegador.");
        }
        catch (Exception ex)
        {
            ShowError($"Nao foi possivel abrir o link: {ex.Message}");
        }
    }

    private async void UpdateLauncherButton_Click(object sender, RoutedEventArgs e)
    {
        if (isBusy)
        {
            SetStatus("Aguarde a operacao atual terminar antes de atualizar o launcher.");
            return;
        }

        if (availableUpdate is null)
        {
            SetStatus("Nenhuma atualizacao do launcher disponivel no momento.");
            return;
        }

        if (runningMinecraftProcess is not null && !runningMinecraftProcess.HasExited)
        {
            MessageBox.Show(
                this,
                "Feche o Minecraft antes de atualizar o launcher. Assim evitamos arquivo travado e instalacao incompleta.",
                "Cobblemon Legacy",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (downloadedUpdate is not null
            && string.Equals(downloadedUpdate.TagName, availableUpdate.TagName, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(downloadedUpdateInstallerPath)
            && File.Exists(downloadedUpdateInstallerPath))
        {
            var installDownloaded = MessageBox.Show(
                this,
                $"O instalador {availableUpdate.TagName} ja esta baixado. Fechar o launcher e instalar agora?",
                "Cobblemon Legacy",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (installDownloaded == MessageBoxResult.Yes)
            {
                LauncherUpdateService.StartInstallerAndExit(downloadedUpdateInstallerPath);
                Close();
            }

            return;
        }

        var result = MessageBox.Show(
            this,
            BuildUpdatePrompt(availableUpdate),
            "Cobblemon Legacy",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            SetBusy(true);
            UpdateLauncherButton.IsEnabled = false;
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 0;
            SetStatus($"Baixando launcher {availableUpdate.Version}...");
            LauncherRuntime.WriteTelemetry("launcher_update_download_start", new Dictionary<string, object?>
            {
                ["version"] = availableUpdate.TagName,
                ["size"] = availableUpdate.Size
            });

            var installerPath = await LauncherUpdateService.DownloadInstallerAsync(http, availableUpdate, SetByteProgress);
            downloadedUpdate = availableUpdate;
            downloadedUpdateInstallerPath = installerPath;
            UpdateLauncherButton.Content = $"Instalar {availableUpdate.Version}";
            SetStatus("Instalador baixado e verificado.");
            LauncherRuntime.WriteTelemetry("launcher_update_download_end", new Dictionary<string, object?>
            {
                ["version"] = availableUpdate.TagName,
                ["path"] = installerPath
            });

            var installNow = MessageBox.Show(
                this,
                "Instalador baixado. Fechar o launcher e instalar agora?",
                "Cobblemon Legacy",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (installNow == MessageBoxResult.Yes)
            {
                LauncherUpdateService.StartInstallerAndExit(installerPath);
                Close();
            }
        }
        catch (Exception ex)
        {
            ShowError($"Nao foi possivel atualizar o launcher: {ex.Message}");
            UpdateLauncherButton.IsEnabled = true;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void NewsLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(currentNews?.Url))
        {
            OpenExternalUrl(currentNews.Url);
            return;
        }

        SetStatus("Este aviso nao possui link externo.");
    }

    private static string BuildUpdatePrompt(LauncherUpdateInfo update)
    {
        var builder = new StringBuilder()
            .AppendLine($"Nova versao disponivel: {update.Version}")
            .AppendLine($"Tamanho: {LauncherRuntime.FormatBytes(update.Size)}")
            .AppendLine()
            .AppendLine("Baixar e abrir o instalador agora?");

        var notes = (update.Body ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(notes))
        {
            if (notes.Length > 600)
                notes = $"{notes[..600]}...";

            builder
                .AppendLine()
                .AppendLine("Notas:")
                .AppendLine(notes);
        }

        return builder.ToString();
    }

    private void NewsAllButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new NewsWindow(newsFeed ?? new LauncherNewsFeed())
            {
                Owner = this
            };

            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            ShowError($"Nao foi possivel abrir os avisos: {ex.Message}");
        }
    }

    private void OpenFileLocation(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
                    return;
                }
            }
            catch (Exception ex)
            {
                ShowError($"Nao foi possivel abrir a pasta: {ex.Message}");
                return;
            }

            ShowError("Nao foi possivel abrir a pasta do arquivo.");
        }
    }

    private void TryCopyToClipboard(string text, string successMessage)
    {
        try
        {
            Clipboard.SetText(text);
            SetStatus(successMessage);
        }
        catch (Exception ex)
        {
            ShowError($"Nao foi possivel copiar para a area de transferencia: {ex.Message}");
        }
    }

    private async Task RefreshServerStatusLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshServerStatusAsync(cancellationToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

            while (await timer.WaitForNextTickAsync(cancellationToken))
                await RefreshServerStatusAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshServerStatusAsync(CancellationToken cancellationToken)
    {
        var status = await MinecraftServerStatusClient.QueryAsync(LauncherRuntime.ServerIp, cancellationToken);

        Dispatcher.Invoke(() =>
        {
            ServerPlayersText.Text = status.ToDisplayText();
            ServerPlayersText.ToolTip = status.ToToolTipText();
        });
    }

    private async Task CheckLauncherUpdateInBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            var update = await LauncherUpdateService.CheckForUpdateAsync(http, LauncherRuntime.LauncherVersion);
            if (cancellationToken.IsCancellationRequested || update is null)
                return;

            availableUpdate = update;
            Dispatcher.Invoke(() =>
            {
                UpdateLauncherButton.Content = $"Atualizar {update.Version}";
                UpdateLauncherButton.Visibility = Visibility.Visible;
                SetStatus($"Nova versao do launcher disponivel: {update.Version}.");
            });
        }
        catch (Exception ex)
        {
            AppendLog($"Nao foi possivel verificar update do launcher: {ex.Message}");
        }
    }

    private async Task LoadNewsInBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            var feed = await LauncherNewsService.LoadFeedAsync(http, LauncherRuntime.JsonOptions);
            var news = feed.Items.FirstOrDefault();
            if (cancellationToken.IsCancellationRequested || news is null)
                return;

            newsFeed = feed;
            currentNews = news;
            Dispatcher.Invoke(() => ApplyNewsToUi(news));
        }
        catch (Exception ex)
        {
            AppendLog($"Nao foi possivel carregar avisos: {ex.Message}");
        }
    }

    private void ApplyNewsToUi(LauncherNewsItem news)
    {
        NewsTitleText.Text = string.IsNullOrWhiteSpace(news.Title) ? "Cobblemon Legacy" : news.Title.Trim();
        NewsBodyText.Text = news.Message.Trim();
        NewsLinkButton.Visibility = string.IsNullOrWhiteSpace(news.Url) ? Visibility.Collapsed : Visibility.Visible;
        NewsPanel.Visibility = Visibility.Visible;
    }

    private void WindowChrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsFromInteractiveControl(e.OriginalSource as DependencyObject))
            return;

        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowNicknameEditor(bool visible)
    {
        NicknameEditorPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (visible)
        {
            NicknameTextBox.Focus();
            NicknameTextBox.SelectAll();
        }
    }

    private void SetPrimaryAction(LauncherPrimaryAction action)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetPrimaryAction(action));
            return;
        }

        primaryAction = action;
        PlayButton.Content = action switch
        {
            LauncherPrimaryAction.Checking => "VERIFICANDO...",
            LauncherPrimaryAction.NeedsUpdate => "ATUALIZAR",
            LauncherPrimaryAction.Ready => "JOGAR",
            LauncherPrimaryAction.Updating => "ATUALIZANDO...",
            LauncherPrimaryAction.Launching => "ABRINDO...",
            _ => "JOGAR"
        };

        PlayButton.Visibility = action == LauncherPrimaryAction.Hidden ? Visibility.Collapsed : Visibility.Visible;
        PlayButton.IsEnabled = !isBusy && IsPrimaryActionClickable(action);
    }

    private static bool IsPrimaryActionClickable(LauncherPrimaryAction action)
    {
        return action is LauncherPrimaryAction.NeedsUpdate or LauncherPrimaryAction.Ready;
    }

    private static bool IsFromInteractiveControl(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBoxBase or System.Windows.Controls.ProgressBar)
                return true;

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void SetBusy(bool value)
    {
        isBusy = value;
        PlayButton.IsEnabled = !value && IsPrimaryActionClickable(primaryAction);
        UpdateLauncherButton.IsEnabled = !value && availableUpdate is not null;
        ProgressBar.IsIndeterminate = value;
        ProgressPercentText.Text = value ? "..." : $"{ProgressBar.Value:0}%";
    }

    private void SetIntegrityStatus(string message)
    {
        Dispatcher.Invoke(() => IntegrityText.Text = message);
    }

    private void SetStatus(string message)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = message;
            AppendLog(message);
        });
    }

    private void SetByteProgress(long current, long total)
    {
        Dispatcher.Invoke(() =>
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = Math.Clamp(current * 100d / total, 0, 100);
            ProgressPercentText.Text = $"{ProgressBar.Value:0}%";
            StatusText.Text = $"Download: {LauncherRuntime.FormatBytes(current)} / {LauncherRuntime.FormatBytes(total)}";
        });
    }

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendLog(message));
            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        WriteUiLog(line);
        LogTextBox.AppendText(line);
        TrimVisibleLog();
        LogTextBox.ScrollToEnd();
    }

    private void TrimVisibleLog()
    {
        if (LogTextBox.Text.Length <= MaxVisibleLogCharacters)
            return;

        var text = LogTextBox.Text;
        var start = text.Length - MaxVisibleLogCharacters;
        var nextLine = text.IndexOf(Environment.NewLine, start, StringComparison.Ordinal);
        if (nextLine >= 0)
            start = nextLine + Environment.NewLine.Length;

        LogTextBox.Text = text[start..];
        LogTextBox.CaretIndex = LogTextBox.Text.Length;
    }

    private static void WriteUiLog(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UiLogPath)!);
            if (File.Exists(UiLogPath) && new FileInfo(UiLogPath).Length > MaxUiLogBytes)
                File.WriteAllText(UiLogPath, "", Encoding.UTF8);

            File.AppendAllText(UiLogPath, line, Encoding.UTF8);
        }
        catch
        {
            // File logging is diagnostic only; the launcher UI should keep working.
        }
    }

    private void ShowError(string message)
    {
        LauncherRuntime.WriteTelemetry("ui_error", new Dictionary<string, object?> { ["message"] = message });
        SetStatus($"Erro: {message}");
        MessageBox.Show(this, message, "Cobblemon Legacy", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

internal enum LauncherPrimaryAction
{
    Hidden,
    Checking,
    NeedsUpdate,
    Ready,
    Updating,
    Launching
}
