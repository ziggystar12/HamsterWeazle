using System.Windows;

namespace HamsterWeazle;

public partial class App : Application
{
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
