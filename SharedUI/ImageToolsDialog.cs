using System.IO;
using HamsterWeazle.Services;
#if WINDOWS
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
#else
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
#endif

namespace HamsterWeazle;

public sealed class ImageToolsDialog : Window
{
    private sealed record DosFormatChoice(string Code, string Label) { public override string ToString() => Label; }
    private Fat12Image? _image;
    private readonly ListBox _files = new();
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontSize = 12 };
    private readonly Action _refreshOwner;
    private readonly Button _import, _save, _extract;

    private ImageToolsDialog(string? path, Action refresh)
    {
        _refreshOwner = refresh;
        Title = "Disk Image Tools"; Width = 820; Height = 520; MinWidth = 700; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
#if WINDOWS
        SetResourceReference(BackgroundProperty, "Win.Panel"); SetResourceReference(ForegroundProperty, "Win.Text");
        SetResourceReference(FontFamilyProperty, "Win.FontMain");
        _files.SetResourceReference(BackgroundProperty, "Win.Input.BG"); _files.SetResourceReference(ForegroundProperty, "Win.Text");
        _files.SetResourceReference(BorderBrushProperty, "Win.Input.Border");
#else
        if (Application.Current!.TryGetResource("Win.Panel", null, out var background) && background is IBrush bg) Background = bg;
        if (Application.Current!.TryGetResource("Win.Text", null, out var foreground) && foreground is IBrush fg)
        { Foreground = fg; _files.Foreground = fg; _info.Foreground = fg; _status.Foreground = fg; }
        if (Application.Current!.TryGetResource("Win.Input.BG", null, out var inputBg) && inputBg is IBrush ib) _files.Background = ib;
#endif
        var root = new DockPanel();
        var frame = new Border { Padding = new Thickness(16), Child = root };
#if WINDOWS
        frame.SetResourceReference(BackgroundProperty, "Win.Panel");
#else
        frame.Background = Background;
#endif
        Content = frame;
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        var open = MakeButton("Open image...");
        open.Click += async (_, _) =>
        {
            string? file = await PickOpenAsync(this, "Open DOS FAT12 image", ["*.img", "*.ima", "*.st", "*.vfd", "*.flp"]);
            if (file != null && (_image?.Modified != true || await ConfirmAsync(this, "Discard the unsaved image edits and open another disk?"))) LoadImage(file);
        };
        var formats = new ComboBox { Width = 145, Margin = new Thickness(8, 0, 8, 0), ItemsSource = new[] {
            new DosFormatChoice("ibm.360", "5.25 inch 360 KB"), new DosFormatChoice("ibm.720", "3.5 inch 720 KB"),
            new DosFormatChoice("ibm.1200", "5.25 inch 1.2 MB"), new DosFormatChoice("ibm.1440", "3.5 inch 1.44 MB"),
            new DosFormatChoice("ibm.2880", "3.5 inch 2.88 MB") }, SelectedIndex = 3 };
        var create = MakeButton("New DOS image");
        create.Click += async (_, _) =>
        {
            if (_image?.Modified == true && !await ConfirmAsync(this, "Discard the unsaved image edits and create a new disk?")) return;
            Guard(() => { _image = Fat12Image.Create((formats.SelectedItem as DosFormatChoice)?.Code ?? "ibm.1440"); RefreshFiles(); _status.Text = "New data disk. Add files, then save a copy."; });
        };
        var report = MakeButton("Capture report...");
        report.Click += async (_, _) =>
        {
            try
            {
                string? image = _image?.SourcePath ?? await PickOpenAsync(this, "Choose image for capture report", ["*.*"]);
                if (image != null) await ShowTextAsync(this, "Capture report", CaptureStore.FindForImage(image, readOnly: false)?.ToText() ?? "No matching capture report was found.");
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        top.Children.Add(open); top.Children.Add(formats); top.Children.Add(create); top.Children.Add(report);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        _import = MakeButton("Add files..."); _save = MakeButton("Save copy..."); _extract = MakeButton("Extract file...");
        _import.Click += async (_, _) =>
        {
            try
            {
                if (_image == null) return;
                var paths = await PickFilesAsync(this);
                if (paths.Count == 0) return;
                var names = _image.ImportFiles(paths);
                RefreshFiles(); _status.Text = "Added to the image root as DOS names: " + string.Join(", ", names) + ". Save a copy to keep these changes.";
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        _save.Click += async (_, _) =>
        {
            try
            {
                if (_image == null) return;
                string name = _image.SourcePath == null ? "DOS-disk.img" : Path.GetFileNameWithoutExtension(_image.SourcePath) + "-edited.img";
                string? path = await PickSaveAsync(this, "Save a new image copy", name);
                if (path == null) return;
                _image.SaveCopy(path); _status.Text = "Saved " + Path.GetFileName(path); _refreshOwner();
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        _extract.Click += async (_, _) =>
        {
            try
            {
                if (_image == null || _files.SelectedItem is not Fat12Entry entry) { _status.Text = "Select a file to extract."; return; }
                byte[] data = _image.ReadFile(entry);
                string? path = await PickSaveAsync(this, "Extract file to a new copy", entry.Name);
                if (path == null) return;
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write); output.Write(data);
                _status.Text = "Extracted " + entry.Path;
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        actions.Children.Add(_import); actions.Children.Add(_save); actions.Children.Add(_extract);
        DockPanel.SetDock(actions, Dock.Top); root.Children.Add(actions);
        DockPanel.SetDock(_info, Dock.Top); root.Children.Add(_info);
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        root.Children.Add(_files);
        Closing += (_, e) =>
        {
            // Edits have never been written to the source. Keep unsaved work visible until the user chooses.
#if WINDOWS
            if (_image?.Modified == true && MessageBox.Show(this, "Discard the unsaved image edits?", "Disk Image Tools", MessageBoxButton.YesNo) != MessageBoxResult.Yes) e.Cancel = true;
#else
            if (_image?.Modified == true && !_discardConfirmed)
            {
                e.Cancel = true;
                _ = ConfirmDiscardAsync();
            }
#endif
        };
        RefreshFiles();
        if (path != null) LoadImage(path);
    }
#if !WINDOWS
    private bool _discardConfirmed;
    private async Task ConfirmDiscardAsync()
    {
        if (await ConfirmAsync(this, "Discard the unsaved image edits?")) { _discardConfirmed = true; Close(); }
    }
#endif
    private void LoadImage(string path) => Guard(() =>
    {
        var loaded = Fat12Image.Open(path);
        loaded.Entries(); // Validate directories before replacing the current image and its edits.
        _image = loaded;
        RefreshFiles(); _status.Text = "Browse or extract files. Edits are saved through Save copy.";
    });
    private void Guard(Action action) { try { action(); } catch (Exception ex) { _status.Text = ex.Message; } }
    private void RefreshFiles()
    {
        _files.ItemsSource = _image?.Entries() ?? [];
        _info.Text = _image == null ? "Open a DOS FAT12 floppy image or create a blank data disk."
            : $"{(_image.SourcePath == null ? "New image" : Path.GetFileName(_image.SourcePath))} · {_image.Bpb.TotalSectors * (long)_image.Bpb.BytesPerSector / 1024:N0} KB · {_image.FreeBytes:N0} bytes free";
        _import.IsEnabled = _save.IsEnabled = _extract.IsEnabled = _image != null;
    }
    private static Button MakeButton(string text)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0) };
#if WINDOWS
        button.SetResourceReference(StyleProperty, "GhostBtn");
#else
        button.Classes.Add("ghost");
#endif
        return button;
    }
    public static async Task OpenAsync(Window owner, string? path, Action refresh)
    {
        var dialog = new ImageToolsDialog(path, refresh);
#if WINDOWS
        dialog.Owner = owner; dialog.ShowDialog(); await Task.CompletedTask;
#else
        await dialog.ShowDialog(owner);
#endif
    }
    public static Task<string?> PickOpenAsync(Window owner, string title, string[] patterns)
    {
#if WINDOWS
        var picker = new OpenFileDialog { Title = title, Filter = "Files|" + string.Join(';', patterns) + "|All files|*.*" };
        return Task.FromResult(picker.ShowDialog(owner) == true ? picker.FileName : null);
#else
        return Pick();
        async Task<string?> Pick()
        {
            var paths = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Files") { Patterns = patterns }, FilePickerFileTypes.All] });
            return paths.FirstOrDefault()?.TryGetLocalPath();
        }
#endif
    }
    private static async Task<IReadOnlyList<string>> PickFilesAsync(Window owner)
    {
#if WINDOWS
        var picker = new OpenFileDialog { Title = "Add files to image root", Multiselect = true, Filter = "All files|*.*" };
        return await Task.FromResult<IReadOnlyList<string>>(picker.ShowDialog(owner) == true ? picker.FileNames : []);
#else
        var paths = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Add files to image root", AllowMultiple = true });
        return paths.Select(p => p.TryGetLocalPath()).OfType<string>().ToArray();
#endif
    }
    private static async Task<string?> PickSaveAsync(Window owner, string title, string filename)
    {
#if WINDOWS
        var picker = new SaveFileDialog { Title = title, FileName = filename, OverwritePrompt = false };
        return await Task.FromResult(picker.ShowDialog(owner) == true ? picker.FileName : null);
#else
        var path = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = filename, ShowOverwritePrompt = false });
        return path?.TryGetLocalPath();
#endif
    }
    public static async Task<bool> ConfirmAsync(Window owner, string message)
    {
#if WINDOWS
        return await Task.FromResult(MessageBox.Show(owner, message, "HamsterWeazle", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
#else
        var window = new Window { Title = "HamsterWeazle", Width = 510, Height = 245, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var content = new StackPanel { Spacing = 18 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var yes = MakeButton("Continue");
        var no = MakeButton("Cancel");
        yes.Click += (_, _) => window.Close(true); no.Click += (_, _) => window.Close(false);
        buttons.Children.Add(yes); buttons.Children.Add(no); content.Children.Add(buttons);
        window.Content = new Border { Padding = new Thickness(20), Child = content };
        return await window.ShowDialog<bool>(owner);
#endif
    }
    public static async Task ShowTextAsync(Window owner, string title, string text)
    {
        var box = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap };
        var window = new Window { Title = title, Width = 850, Height = 580, MinWidth = 450, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = box };
#if WINDOWS
        box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; box.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        window.Owner = owner; window.ShowDialog(); await Task.CompletedTask;
#else
        await window.ShowDialog(owner);
#endif
    }
}
