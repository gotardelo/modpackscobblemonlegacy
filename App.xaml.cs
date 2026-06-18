using System.Windows;

namespace CobblemonLegacy;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(arg => string.Equals(arg, "configure-profile", StringComparison.OrdinalIgnoreCase)))
        {
            _ = ConfigureProfileAndShutdownAsync();
            return;
        }

        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();
    }

    private async Task ConfigureProfileAndShutdownAsync()
    {
        try
        {
            var settings = await LauncherSettings.LoadAsync(LauncherRuntime.JsonOptions);
            var gameDir = LauncherRuntime.ExpandGameDirectory(settings);
            await MinecraftProfileConfigurator.ConfigureAsync(gameDir, settings);
            Shutdown(0);
        }
        catch
        {
            Shutdown(1);
        }
    }
}
