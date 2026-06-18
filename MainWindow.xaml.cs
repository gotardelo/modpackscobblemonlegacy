using System.Net.Http;
using System.Text.RegularExpressions;
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
    private static readonly Regex NicknameRegex = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);

    private readonly string[] startupArgs;
    private readonly HttpClient http;
    private LauncherSettings? settings;
    private ModpackManifest? manifest;
    private bool isBusy;

    public MainWindow(string[] args)
    {
        startupArgs = args;
        http = LauncherRuntime.CreateHttpClient();
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => http.Dispose();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true);
            AppendLog("Inicializando launcher...");

            settings = await LauncherSettings.LoadAsync(LauncherRuntime.JsonOptions);
            await EnsureAuthChoiceAsync();
            ApplySettingsToUi();
            await LoadManifestAsync();

            SetStatus("Pronto para jogar.");
            SetBusy(false);

            if (startupArgs.Any(arg => string.Equals(arg, "play", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "jogar", StringComparison.OrdinalIgnoreCase)))
                await PlayAsync();
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
        {
            settings.AuthMode = AuthModes.Offline;
            settings.OfflineUsername = "Player";
        }
        else
        {
            settings.AuthMode = dialog.SelectedAuthMode;
            if (settings.AuthMode == AuthModes.Offline)
                settings.OfflineUsername = dialog.OfflineNickname;
        }

        await settings.SaveAsync(LauncherRuntime.JsonOptions);
    }

    private async Task LoadManifestAsync()
    {
        if (settings is null)
            return;

        manifest = await ModpackManifestLoader.LoadAsync(http, settings.ManifestUrl, LauncherRuntime.JsonOptions, AppendLog);
        var mods = manifest.Files.Count(file => file.Path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase));
        var resourcepacks = manifest.Files.Count(file => file.Path.StartsWith("resourcepacks/", StringComparison.OrdinalIgnoreCase));

        ManifestText.Text = $"v{manifest.Version}";
        PackSummaryText.Text = $"{mods} mods | {resourcepacks} resourcepacks";
        AppendLog($"Manifest carregado: {mods} mods, {resourcepacks} resourcepacks.");
    }

    private void ApplySettingsToUi()
    {
        if (settings is null)
            return;

        NicknameTextBox.Text = settings.OfflineUsername;
        RamText.Text = $"RAM: {settings.MaximumRamMb} MB";
        UpdateAccountText();
    }

    private void UpdateAccountText()
    {
        if (settings is null)
            return;

        AccountText.Text = settings.AuthMode switch
        {
            AuthModes.Microsoft when !string.IsNullOrWhiteSpace(settings.MicrosoftUsername) => $"Microsoft: {settings.MicrosoftUsername}",
            AuthModes.Microsoft => "Microsoft selecionado",
            AuthModes.Offline => $"Nickname: {settings.OfflineUsername}",
            _ => "Nenhuma conta selecionada"
        };
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        await PlayAsync();
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await UpdatePackAsync(launchAfterUpdate: false);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            ShowError(ex.Message);
        }
    }

    private async Task PlayAsync()
    {
        try
        {
            var versionId = await UpdatePackAsync(launchAfterUpdate: true);
            if (versionId is null || settings is null)
                return;

            var session = await ResolveSessionAsync();
            var gameDir = LauncherRuntime.ExpandGameDirectory(settings);
            var launcher = LauncherRuntime.CreateMinecraftLauncher(gameDir, SetStatus, SetByteProgress);

            SetStatus("Abrindo Minecraft...");
            var process = await LauncherRuntime.StartGameAsync(launcher, versionId, settings, session);
            AppendLog($"Minecraft iniciado com PID {process.Id}.");
            SetStatus("Minecraft aberto. A primeira carga pode levar alguns minutos.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<string?> UpdatePackAsync(bool launchAfterUpdate)
    {
        if (settings is null)
            return null;

        if (isBusy)
            return null;

        SetBusy(true);
        ProgressBar.IsIndeterminate = true;

        await SaveOfflineNicknameIfNeededAsync();

        manifest ??= await ModpackManifestLoader.LoadAsync(http, settings.ManifestUrl, LauncherRuntime.JsonOptions, AppendLog);

        var gameDir = LauncherRuntime.ExpandGameDirectory(settings);
        var launcher = LauncherRuntime.CreateMinecraftLauncher(gameDir, SetStatus, SetByteProgress);

        var versionId = await LauncherRuntime.InstallOrUpdateAsync(http, launcher, manifest, gameDir, SetStatus, SetByteProgress);
        await MinecraftProfileConfigurator.ConfigureAsync(gameDir, settings, SetStatus);

        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = 100;
        SetStatus(launchAfterUpdate ? "Pack atualizado. Preparando jogo..." : "Pack atualizado.");

        if (!launchAfterUpdate)
            SetBusy(false);

        return versionId;
    }

    private async Task<MSession> ResolveSessionAsync()
    {
        if (settings is null)
            throw new InvalidOperationException("Configuracao nao carregada.");

        if (settings.AuthMode == AuthModes.Microsoft)
        {
            SetStatus("Abrindo login Microsoft...");
            AppendLog("Login Microsoft iniciado.");
            var loginHandler = JELoginHandlerBuilder.BuildDefault();
            var session = await loginHandler.Authenticate();
            settings.MicrosoftUsername = string.IsNullOrWhiteSpace(session.Username) ? "Conta Microsoft" : session.Username;
            await settings.SaveAsync(LauncherRuntime.JsonOptions);
            UpdateAccountText();
            return session;
        }

        if (!NicknameRegex.IsMatch(settings.OfflineUsername))
            throw new InvalidOperationException("Defina um nickname valido antes de jogar.");

        return MSession.CreateOfflineSession(settings.OfflineUsername);
    }

    private async Task SaveOfflineNicknameIfNeededAsync()
    {
        if (settings is null || settings.AuthMode != AuthModes.Offline)
            return;

        var nickname = NicknameTextBox.Text.Trim();
        if (!NicknameRegex.IsMatch(nickname))
            throw new InvalidOperationException("Nickname invalido. Use 3 a 16 caracteres: letras, numeros ou underline.");

        settings.OfflineUsername = nickname;
        await settings.SaveAsync(LauncherRuntime.JsonOptions);
        UpdateAccountText();
    }

    private async void SaveNicknameButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (settings is null)
                return;

            settings.AuthMode = AuthModes.Offline;
            await SaveOfflineNicknameIfNeededAsync();
            SetStatus("Nickname salvo.");
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

            settings.AuthMode = AuthModes.Microsoft;
            await settings.SaveAsync(LauncherRuntime.JsonOptions);
            UpdateAccountText();
            SetStatus("Microsoft selecionado. O login abre quando voce clicar em Jogar.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void OfflineModeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (settings is null)
                return;

            settings.AuthMode = AuthModes.Offline;
            await SaveOfflineNicknameIfNeededAsync();
            SetStatus("Modo nickname selecionado.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void CopyIpButton_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(LauncherRuntime.ServerIp);
        SetStatus("IP copiado.");
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
        PlayButton.IsEnabled = !value;
        ProgressBar.IsIndeterminate = value;
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

        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogTextBox.ScrollToEnd();
    }

    private void ShowError(string message)
    {
        SetStatus($"Erro: {message}");
        MessageBox.Show(this, message, "Cobblemon Legacy", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
