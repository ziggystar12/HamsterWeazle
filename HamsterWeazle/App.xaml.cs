using System.Windows;
using HamsterWeazle.Services;

namespace HamsterWeazle;

public partial class App : Application
{
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
}
