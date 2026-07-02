using System.Windows;
using HamsterWeazle.Services;

namespace HamsterWeazle;

public partial class App : Application
{
    static App()
    {
        EnsureWpfWindirEnvironment();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        GwRunner.GwExeFinder = UpdateChecker.FindGwExe;
        base.OnStartup(e);
    }

    public static void SwitchTheme(string themeName)
    {
        var uri = themeName == "Amiga"
            ? new Uri("pack://application:,,,/Themes/AmigaTheme.xaml")
            : new Uri("pack://application:,,,/Themes/DarkTheme.xaml");

        var dict = new ResourceDictionary { Source = uri };
        Current.Resources.MergedDictionaries.Clear();
        Current.Resources.MergedDictionaries.Add(dict);
    }

    private static void EnsureWpfWindirEnvironment()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
            return;

        string? windowsDir = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrWhiteSpace(windowsDir))
            windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        if (string.IsNullOrWhiteSpace(windowsDir) && !string.IsNullOrWhiteSpace(Environment.SystemDirectory))
            windowsDir = System.IO.Directory.GetParent(Environment.SystemDirectory)?.FullName;

        if (!string.IsNullOrWhiteSpace(windowsDir))
            Environment.SetEnvironmentVariable("windir", windowsDir);
    }
}
