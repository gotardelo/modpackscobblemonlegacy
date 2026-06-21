using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace CobblemonLegacy;

public partial class OptionsWindow : Window
{
    public LauncherSettings Settings { get; }
    private bool isUpdatingRamControls;
    private readonly string originalPerformanceProfile;

    public OptionsWindow(LauncherSettings settings)
    {
        Settings = settings.Clone();
        originalPerformanceProfile = Settings.PerformanceProfile;
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
        RamSlider.Value = Settings.MaximumRamMb;
        CompatibilityModeCheckBox.IsChecked = Settings.CompatibilityMode;
        PerformanceProfileComboBox.SelectedValue = Settings.PerformanceProfile;
        ResourcepackProfileComboBox.SelectedValue = Settings.ResourcepackProfile;
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
            Settings.PerformanceProfile = PerformanceProfileComboBox.SelectedValue as string
                ?? PerformanceProfiles.Auto;
            Settings.ResourcepackProfile = ResourcepackProfileComboBox.SelectedValue as string
                ?? ResourcepackProfiles.Full;
            Settings.LauncherVisibility = LauncherVisibilityComboBox.SelectedValue as string
                ?? LauncherVisibilityModes.HideUntilGameExits;
            Settings.UseIntegratedJava = UseIntegratedJavaCheckBox.IsChecked == true;
            Settings.JavaPath = JavaPathTextBox.Text.Trim();
            Settings.ExtraJvmArguments = ExtraJvmArgumentsTextBox.Text.Trim();

            ValidateGameDirectory(Settings.GameDirectory);
            ValidateJavaSettings(Settings);
            if (!string.Equals(Settings.PerformanceProfile, originalPerformanceProfile, StringComparison.OrdinalIgnoreCase))
                Settings.PerformancePresetVersion = 0;

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
        SetRamValue(LauncherRuntime.GetRecommendedMaximumRamMb());
    }

    private void LowPresetButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyPreset(854, 480, Math.Min(3072, LauncherRuntime.GetRecommendedMaximumRamMb()), compatibilityMode: true);
        PerformanceProfileComboBox.SelectedValue = PerformanceProfiles.Low;
        ResourcepackProfileComboBox.SelectedValue = ResourcepackProfiles.Essential;
    }

    private void BalancedPresetButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyPreset(1280, 720, LauncherRuntime.GetRecommendedMaximumRamMb(), compatibilityMode: false);
        PerformanceProfileComboBox.SelectedValue = PerformanceProfiles.Balanced;
        ResourcepackProfileComboBox.SelectedValue = ResourcepackProfiles.Balanced;
    }

    private void HighPresetButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyPreset(1600, 900, Math.Max(4096, LauncherRuntime.GetRecommendedMaximumRamMb()), compatibilityMode: false);
        PerformanceProfileComboBox.SelectedValue = PerformanceProfiles.High;
        ResourcepackProfileComboBox.SelectedValue = ResourcepackProfiles.Full;
    }

    private void ApplyPreset(int width, int height, int ramMb, bool compatibilityMode)
    {
        WindowWidthTextBox.Text = width.ToString();
        WindowHeightTextBox.Text = height.ToString();
        FullScreenCheckBox.IsChecked = false;
        CompatibilityModeCheckBox.IsChecked = compatibilityMode;
        SetRamValue(Math.Clamp(ramMb, 2048, 8192));
    }

    private void RamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (isUpdatingRamControls || RamTextBox is null)
            return;

        RamTextBox.Text = ((int)e.NewValue).ToString();
    }

    private void SetRamValue(int ramMb)
    {
        isUpdatingRamControls = true;
        try
        {
            var value = Math.Clamp(ramMb, 2048, 8192);
            RamTextBox.Text = value.ToString();
            RamSlider.Value = value;
        }
        finally
        {
            isUpdatingRamControls = false;
        }
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
