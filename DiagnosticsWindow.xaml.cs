using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace CobblemonLegacy;

public partial class DiagnosticsWindow : Window
{
    private readonly DiagnosticsSnapshot snapshot;
    private readonly Func<Task<SupportPackageResult>> createSupportPackage;

    public DiagnosticsWindow(
        DiagnosticsSnapshot snapshot,
        Func<Task<SupportPackageResult>> createSupportPackage)
    {
        this.snapshot = snapshot;
        this.createSupportPackage = createSupportPackage;

        InitializeComponent();
        GeneratedAtText.Text = $"Gerado em {snapshot.CreatedAt:dd/MM/yyyy HH:mm:ss}";
        DiagnosticsGrid.ItemsSource = snapshot.Items;
        StatusText.Text = "Pronto para copiar ou gerar pacote de suporte.";
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        TryCopyToClipboard(BuildPlainText(), "Diagnostico copiado para a area de transferencia.");
    }

    private async void CreatePackageButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Gerando pacote ZIP...";
            var package = await createSupportPackage();
            TryCopyToClipboard(package.Text, "Relatorio copiado para a area de transferencia.");
            OpenFileLocation(package.ZipPath);
            StatusText.Text = "Pacote ZIP criado, relatorio copiado e pasta aberta.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Nao foi possivel gerar ZIP: {ex.Message}";
            MessageBox.Show(this, ex.Message, "Cobblemon Legacy", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void DiscordButton_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalUrl("https://discord.gg/sETS2Fc7Ey", "Discord aberto para suporte.");
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private string BuildPlainText()
    {
        var builder = new StringBuilder()
            .AppendLine("COBBLEMON LEGACY - DIAGNOSTICO")
            .AppendLine($"Gerado em: {snapshot.CreatedAt:yyyy-MM-dd HH:mm:ss}")
            .AppendLine();

        foreach (var item in snapshot.Items)
            builder.AppendLine($"[{item.State}] {item.Name}: {item.Value}");

        return builder.ToString();
    }

    private static void OpenFileLocation(string path)
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
                    Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            }
            catch
            {
                // The caller already keeps the support package path in the status/log context.
            }
        }
    }

    private void TryCopyToClipboard(string text, string successMessage)
    {
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = successMessage;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Nao foi possivel copiar: {ex.Message}";
            MessageBox.Show(this, StatusText.Text, "Cobblemon Legacy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenExternalUrl(string url, string successMessage)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
            StatusText.Text = successMessage;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Nao foi possivel abrir link: {ex.Message}";
            MessageBox.Show(this, StatusText.Text, "Cobblemon Legacy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
