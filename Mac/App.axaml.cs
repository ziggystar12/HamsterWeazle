using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using HamsterWeazle.Services;

namespace HamsterWeazle;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        GwRunner.GwExeFinder = UpdateChecker.FindGwExe;

        // Load the saved theme before the window is created so DynamicResources resolve.
        var settings = SettingsManager.Load();
        SwitchTheme(settings.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Replaces the HamsterWeazle theme entry (index 1) in Application.Styles.</summary>
    public static void SwitchTheme(string name)
    {
        var styles = Current!.Styles;

        // Remove all theme entries after FluentTheme (index 0)
        while (styles.Count > 1)
            styles.RemoveAt(styles.Count - 1);

        var uri = name == "Amiga"
            ? new Uri("avares://HamsterWeazle/Themes/AmigaTheme.axaml")
            : new Uri("avares://HamsterWeazle/Themes/DarkTheme.axaml");

        styles.Add(new StyleInclude(new Uri("avares://HamsterWeazle/")) { Source = uri });
    }
}
