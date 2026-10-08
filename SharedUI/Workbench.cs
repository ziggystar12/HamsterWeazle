using HamsterWeazle.Services;
using System.Diagnostics;
using System.IO;
#if WINDOWS
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
#else
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
#endif

namespace HamsterWeazle;

public partial class MainWindow
{
    private bool _showWriteQueue;
    private bool _showSettings;
    private SettingsView? _settingsView;

    private async void BtnSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (_driveIsRunning || _settingsView?.IsInstalling == true)
        {
            BtnSettings.IsChecked = _showSettings;
            return;
        }
        // Keep current operation choices before the page saves individual settings.
        SaveSettings();
        if (_settingsView == null)
        {
            _settingsView = new SettingsView();
            _settingsView.SettingsChanged += SettingsView_SettingsChanged;
            _settingsView.InstallingChanged += SettingsView_InstallingChanged;
            SettingsHost.Content = _settingsView;
        }
        _showSettings = true;
        UpdateWorkbench();
        await _settingsView.RefreshAsync();
    }

    private void SettingsView_SettingsChanged(object? sender, EventArgs e)
    {
        var saved = SettingsManager.Load();
        // Merge page-owned values without losing an in-memory queue or operation choice.
        _settings.Theme = saved.Theme;
        _settings.GwPath = saved.GwPath;
        _settings.HxcPath = saved.HxcPath;
        _settings.HxcInstalledTag = saved.HxcInstalledTag;
#if WINDOWS
        _settings.DmkToolPath = saved.DmkToolPath;
        _settings.DmkInstalledTag = saved.DmkInstalledTag;
#endif
        _runner.GwPath = saved.GwPath;
#if WINDOWS
        string? hxcDir = Path.GetDirectoryName(saved.HxcPath);
        string? hxcCli = string.IsNullOrEmpty(hxcDir) ? null : Path.Combine(hxcDir, "hxcfe.exe");
        _hxcRunner.GwPath = File.Exists(hxcCli) ? hxcCli : UpdateChecker.FindHxcCliExe();
#else
        _hxcRunner.GwPath = UpdateChecker.FindHxcCliExe(Path.GetDirectoryName(saved.HxcPath));
#endif
        SettingsManager.Save(_settings);
        UpdateCommandPreview();
        UpdateWorkbench();
        RefreshSidebar();
        _mapDirty = true;
    }

    private void SettingsView_InstallingChanged(object? sender, EventArgs e)
    {
        bool available = _settingsView?.IsInstalling != true;
        WorkbenchRail.IsEnabled = DiskLibrary.IsEnabled = SettingsHost.IsEnabled = available;
        BtnTheme.IsEnabled = UpdateBanner.IsEnabled = available;
    }

    private void BtnBuyMeACoffee_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://buymeacoffee.com/ziggystar12") { UseShellExecute = true });
        }
        catch (Exception ex) { AppendLog(string.Concat("[error] Could not open the support page: ", ex.Message)); }
    }

    private void WorkbenchLayout_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // Reserve a separate console area; opening it never resizes the operation area.
        WorkspaceScroll.MaxHeight = Math.Max(0, e.NewSize.Height - 80);
    }

    private void BtnTheme_Click(object? sender, RoutedEventArgs e)
    {
#if WINDOWS
        string current = Application.Current.Resources["Win.ThemeName"] as string ?? "Dark";
#else
        string current = Res<string>("Win.ThemeName") ?? "Dark";
#endif
        _settings.Theme = current == "Dark" ? "Light" : "Dark";
        App.SwitchTheme(_settings.Theme);
        _settingsView?.RefreshAppearance();
        SettingsManager.Save(_settings);
        RefreshSidebar();
        _mapDirty = true;
    }

    private void BtnMore_Click(object? sender, RoutedEventArgs e) => MorePopup.IsOpen = !MorePopup.IsOpen;

    private void LibraryTab_Click(object? sender, RoutedEventArgs e)
    {
        _showWriteQueue = ReferenceEquals(sender, LibraryQueueTab);
        LibraryInboxTab.IsChecked = !_showWriteQueue;
        LibraryQueueTab.IsChecked = _showWriteQueue;
#if WINDOWS
        InboxScroll.Visibility = _showWriteQueue ? Visibility.Collapsed : Visibility.Visible;
        QueueScroll.Visibility = _showWriteQueue ? Visibility.Visible : Visibility.Collapsed;
#else
        InboxScroll.IsVisible = !_showWriteQueue;
        QueueScroll.IsVisible = _showWriteQueue;
#endif
        BtnClearLibrary.Content = _showWriteQueue ? "Clear write history" : "Clear Inbox history";
    }

    private void BtnClearLibrary_Click(object? sender, RoutedEventArgs e)
    {
        if (_showWriteQueue) BtnClearWriteQueue_Click(sender!, e);
        else BtnClearInbox_Click(sender!, e);
    }

    private void UpdateWorkbench()
    {
        if (TxtOperationKind == null) return;
#if WINDOWS
        WorkbenchLayout.Visibility = _showSettings ? Visibility.Collapsed : Visibility.Visible;
        SettingsWorkspace.Visibility = _showSettings ? Visibility.Visible : Visibility.Collapsed;
#else
        WorkbenchLayout.IsVisible = !_showSettings;
        SettingsWorkspace.IsVisible = _showSettings;
#endif
        BtnSettings.IsChecked = _showSettings;
        foreach (var tab in new[] { TabRead, TabWrite, TabErase, TabTools, TabInfo })
            tab.IsChecked = !_showSettings && tab.Tag?.ToString() == _currentOp.ToString();
        TxtOperationKind.Text = _currentOp switch
        {
            GwOperation.Read => "READ",
            GwOperation.Write => "WRITE",
            GwOperation.Erase => "ERASE",
            GwOperation.Tools => "TOOLS",
            _ => "DEVICE"
        };
        BtnRun.Content = _currentOp switch
        {
            GwOperation.Read => "Read disk",
            GwOperation.Write => "Write disk",
            GwOperation.Erase => "Erase disk",
            _ => "Check device"
        };
        LblFile.Content = _currentOp == GwOperation.Read ? "Save image as" : "Image to write";
        bool automatic = _currentOp == GwOperation.Read && ChkCustomOutput.IsChecked != true;
        TxtFile.IsReadOnly = automatic;
        TxtSaveHint.Text = automatic ? "Saved to Inbox. Browse to choose another location."
            : _currentOp == GwOperation.Read ? "Custom save location. Original captures are preserved."
            : "Original image stays untouched.";
        BtnClearLog.Content = "Clear activity";
#if WINDOWS
        PanelFile.Visibility = _currentOp is GwOperation.Read or GwOperation.Write ? Visibility.Visible : Visibility.Collapsed;
        PanelDevice.Visibility = _currentOp == GwOperation.Info ? Visibility.Visible : Visibility.Collapsed;
        BtnRun.Visibility = !_driveIsRunning && _currentOp != GwOperation.Tools ? Visibility.Visible : Visibility.Collapsed;
        BtnCancel.Visibility = _driveIsRunning ? Visibility.Visible : Visibility.Collapsed;
#else
        PanelFile.IsVisible = _currentOp is GwOperation.Read or GwOperation.Write;
        PanelDevice.IsVisible = _currentOp == GwOperation.Info;
        BtnRun.IsVisible = !_driveIsRunning && _currentOp != GwOperation.Tools;
        BtnCancel.IsVisible = _driveIsRunning;
#endif
        string device = string.IsNullOrEmpty(_settings.DevicePort) ? "Auto" : _settings.DevicePort;
        TxtDeviceSummary.Text = "Device: " + device;
        TxtFooterDevice.Text = "GreaseWeazle · " + device;
        TxtDriveHeading.Text = "Drive " + (BuildCurrentOptions().Drive ?? "Auto") + " · " + GetDriveFamilyForDisplay() + "″";
    }

    private void UpdateLibraryCounts()
    {
        if (LibraryQueueTab != null) LibraryQueueTab.Content = "Write queue " + _settings.WriteQueueItems.Count;
        if (LibraryInboxTab != null)
        {
            int count = InboxPanel.Children.Count;
            // An empty library contains its explanatory TextBlock.
            if (count == 1 && InboxPanel.Children[0] is TextBlock) count = 0;
            LibraryInboxTab.Content = "Inbox " + count;
        }
    }

    private void QuietLibraryCard(Border card, StackPanel content, Grid actionRow,
        StackPanel buttonRow, TextBlock name, string path, params (Button Button, string Label)[] actions)
    {
        // Keep every existing action, but expose it through one keyboard-accessible menu.
        content.Children.Remove(buttonRow);
        var menu = card.ContextMenu!;
        var items = new List<object>();
        foreach (var (button, label) in actions)
        {
            actionRow.Children.Remove(button);
            var item = new MenuItem { Header = label, IsEnabled = button.IsEnabled };
            item.Click += (_, _) =>
            {
                if (_driveIsRunning || _settingsView?.IsInstalling == true) return;
                _showSettings = false;
                UpdateWorkbench();
#if WINDOWS
                Dispatcher.BeginInvoke(new Action(() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
#else
                Avalonia.Threading.Dispatcher.UIThread.Post(() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
#endif
            };
            items.Add(item);
        }
        var more = new Button { Content = "⋯", Width = 30, Height = 30, Padding = new Thickness(0) };
        Grid.SetColumn(more, 2);
        actionRow.Children.Add(more);
#if WINDOWS
        foreach (var item in menu.Items.Cast<object>().ToArray()) items.Add(item);
        menu.Items.Clear();
        foreach (var item in items) menu.Items.Add(item);
        more.SetResourceReference(Button.StyleProperty, "GhostBtn");
        more.ToolTip = "Image actions";
        card.SetResourceReference(Border.BorderBrushProperty, "Win.Separator");
        card.Background = Brushes.Transparent;
        more.Click += (_, _) => { menu.PlacementTarget = more; menu.IsOpen = true; };
#else
        foreach (var item in menu.ItemsSource!.Cast<object>()) items.Add(item);
        menu.ItemsSource = items;
        more.Classes.Add("ghost");
        ToolTip.SetTip(more, "Image actions");
        card.BorderBrush = Res<IBrush>("Win.Separator");
        card.Background = Brushes.Transparent;
        more.Click += (_, _) => menu.Open(more);
#endif
        card.BorderThickness = new Thickness(0, 0, 0, 1);
        content.Margin = new Thickness(0, 12, 0, 12);
        // The file name opens details; right click and the visible menu expose operations.
        if (name.Parent is not Panel nameParent) return;
        int nameIndex = nameParent.Children.IndexOf(name);
        nameParent.Children.Remove(name);
        var details = new Button { Content = name, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Left };
        Grid.SetColumn(details, 0);
        nameParent.Children.Insert(nameIndex, details);
#if WINDOWS
        details.SetResourceReference(Button.StyleProperty, "GhostBtn");
        details.ToolTip = path;
#else
        details.Classes.Add("ghost");
        ToolTip.SetTip(details, path);
        details.Bind(IsVisibleProperty, new Avalonia.Data.Binding(nameof(TextBlock.IsVisible)) { Source = name });
#endif
        details.Click += async (_, _) =>
        {
            try
            {
                var file = new FileInfo(path);
                string text = file.Name + "\n\n" + file.Length.ToString("N0") + " bytes\n" + path +
                    "\n\n" + (CaptureStore.FindForImage(path, readOnly: false)?.ToText() ?? "No capture report is available for this image.");
                await ImageToolsDialog.ShowTextAsync(this, "Image details", text);
            }
            catch (Exception ex) { AppendLog("[error] " + ex.Message); }
        };
    }

#if !WINDOWS
    private async void BtnCheckForUpdates_Click(object? sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        string version = string.IsNullOrEmpty(_runner.GwPath) ? "" : await GwRunner.GetVersionAsync(_runner.GwPath);
        await CheckForUpdatesAsync(version, userInitiated: true);
    }
#endif
}
