using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace CobblemonLegacy;

public partial class OptionsWindow : Window
{
    public LauncherSettings Settings { get; }

    public OptionsWindow(LauncherSettings settings)
    {
        Settings = settings.Clone();
        InitializeComponent();
        ApplySettingsToUi();
    }

    private void ApplySettingsToUi()
    {
        GameDirectoryTextBox.Text = Settings.GameDirectory;
        WindowWidthTextBox.Text = Settings.WindowWidth.ToString();
        WindowHeightTextBox.Text = Settings.WindowHeight.ToString();
        FullScreenCheckBox.IsChecked = Settings.FullScreen;
        RamTextBox.Text = Settings.MaximumRamMb.ToString();
        CompatibilityModeCheckBox.IsChecked = Settings.CompatibilityMode;
        LauncherVisibilityComboBox.SelectedValue = Settings.LauncherVisibility;
        UseIntegratedJavaCheckBox.IsChecked = Settings.UseIntegratedJava;
        JavaPathTextBox.Text = Settings.JavaPath;
        ExtraJvmArgumentsTextBox.Text = Settings.ExtraJvmArguments;
        UpdateJavaControls();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Settings.GameDirectory = ReadRequiredText(GameDirectoryTextBox.Text, "Diretorio do jogo");
            Settings.WindowWidth = ReadInt(WindowWidthTextBox.Text, "Largura", 640, 3840);
            Settings.WindowHeight = ReadInt(WindowHeightTextBox.Text, "Altura", 360, 2160);
            Settings.FullScreen = FullScreenCheckBox.IsChecked == true;
            Settings.MaximumRamMb = ReadInt(RamTextBox.Text, "RAM", 2048, 8192);
            Settings.CompatibilityMode = CompatibilityModeCheckBox.IsChecked == true;
            Settings.LauncherVisibility = LauncherVisibilityComboBox.SelectedValue as string
                ?? LauncherVisibilityModes.HideUntilGameExits;
            Settings.UseIntegratedJava = UseIntegratedJavaCheckBox.IsChecked == true;
            Settings.JavaPath = JavaPathTextBox.Text.Trim();
            Settings.ExtraJvmArguments = ExtraJvmArgumentsTextBox.Text.Trim();

            ValidateGameDirectory(Settings.GameDirectory);
            ValidateJavaSettings(Settings);

            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cobblemon Legacy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void AutoRamButton_Click(object sender, RoutedEventArgs e)
    {
        RamTextBox.Text = LauncherRuntime.GetRecommendedMaximumRamMb().ToString();
    }

    private void UseIntegratedJavaCheckBox_Click(object sender, RoutedEventArgs e)
    {
        UpdateJavaControls();
    }

    private void BrowseGameDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Escolha o diretorio do jogo",
                InitialDirectory = GetInitialDirectory(GameDirectoryTextBox.Text)
            };

            if (dialog.ShowDialog(this) == true)
                GameDirectoryTextBox.Text = dialog.FolderName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Nao foi possivel abrir o seletor de pasta: {ex.Message}", "Cobblemon Legacy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BrowseJavaButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Escolha java.exe ou javaw.exe",
            Filter = "Java (java.exe; javaw.exe)|java.exe;javaw.exe|Executaveis (*.exe)|*.exe|Todos os arquivos (*.*)|*.*",
            InitialDirectory = GetInitialDirectory(JavaPathTextBox.Text)
        };

        if (dialog.ShowDialog(this) == true)
        {
            UseIntegratedJavaCheckBox.IsChecked = false;
            JavaPathTextBox.Text = dialog.FileName;
            UpdateJavaControls();
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void UpdateJavaControls()
    {
        var useIntegratedJava = UseIntegratedJavaCheckBox.IsChecked == true;
        JavaPathTextBox.IsEnabled = !useIntegratedJava;
    }

    private static string ReadRequiredText(string value, string fieldName)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new InvalidOperationException($"{fieldName} nao pode ficar vazio.");

        return trimmed;
    }

    private static int ReadInt(string value, string fieldName, int min, int max)
    {
        if (!int.TryParse(value.Trim(), out var parsed))
            throw new InvalidOperationException($"{fieldName} precisa ser um numero.");

        if (parsed < min || parsed > max)
            throw new InvalidOperationException($"{fieldName} precisa ficar entre {min} e {max}.");

        return parsed;
    }

    private static void ValidateGameDirectory(string gameDirectory)
    {
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(gameDirectory));
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new InvalidOperationException("Diretorio do jogo invalido.");
    }

    private static void ValidateJavaSettings(LauncherSettings settings)
    {
        if (settings.UseIntegratedJava)
        {
            settings.JavaPath = "";
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.JavaPath))
            throw new InvalidOperationException("Escolha java.exe/javaw.exe ou marque para usar o Java integrado.");

        var javaPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.JavaPath));
        if (!File.Exists(javaPath))
            throw new InvalidOperationException($"Java nao encontrado: {javaPath}");

        var fileName = Path.GetFileName(javaPath);
        if (!fileName.Equals("java.exe", StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Selecione java.exe ou javaw.exe.");
        }

        settings.JavaPath = javaPath;
    }

    private static string GetInitialDirectory(string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
                return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()));
            if (Directory.Exists(fullPath))
                return fullPath;

            var parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                return parent;
        }
        catch
        {
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    }
}
