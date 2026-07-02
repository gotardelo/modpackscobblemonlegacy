using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace CobblemonLegacy;

public partial class NewsWindow : Window
{
    public NewsWindow(LauncherNewsFeed feed)
    {
        InitializeComponent();
        NewsItemsControl.ItemsSource = feed.Items.Count == 0
            ? new[] { new LauncherNewsItem { Title = "Sem avisos", Message = "Nenhum aviso carregado no momento.", PublishedAt = DateTimeOffset.Now } }
            : feed.Items;
    }

    private void DiscordButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://discord.gg/sETS2Fc7Ey")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Nao foi possivel abrir o Discord: {ex.Message}", "Cobblemon Legacy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}
