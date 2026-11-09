using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Proteus.App.ViewModels;

namespace Proteus.App.Views;

public partial class MainWindow : Window, IUiServices
{
    private TextBox? _lastTokenBox;

    public MainWindow()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ListCard.Classes.Set("dragging", false));
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(GotFocusEvent, OnAnyGotFocus, RoutingStrategies.Bubble);
        FileList.KeyDown += OnFileListKeyDown;
        KeyDown += OnWindowKeyDown;
        Closing += (_, e) =>
        {
            if (Vm != null && !Vm.CanClose()) { e.Cancel = true; return; }
            Vm?.SaveSettings();
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm != null) Vm.Ui = this;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Mica only shows through a translucent background.
        if (ActualTransparencyLevel == WindowTransparencyLevel.Mica || ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur)
            this[!BackgroundProperty] = this.GetResourceObservable("WindowMicaBrush").ToBinding();

        // The drawn caption bar keeps room for the (hidden) full-screen button. Pin the
        // remaining buttons to the right edge, like every other Windows app.
        var pinTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        int attempts = 0;
        pinTimer.Tick += (_, _) => { if (PinCaptionButtons() || ++attempts > 40) pinTimer.Stop(); };
        pinTimer.Start();
        this.GetObservable(WindowStateProperty).Subscribe(new Avalonia.Reactive.AnonymousObserver<WindowState>(_ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => PinCaptionButtons(), Avalonia.Threading.DispatcherPriority.Background)));

        if (Environment.GetEnvironmentVariable("PROTEUS_DUMP") is { } dumpPath)
        {
            Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                Visual root = this;
                while (root.GetVisualParent() is { } p) root = p;
                var sb = new System.Text.StringBuilder();
                void Walk(Visual v, int depth)
                {
                    sb.Append(new string(' ', depth * 2)).Append(v.GetType().Name);
                    if (v is StyledElement se && !string.IsNullOrEmpty(se.Name)) sb.Append(" #").Append(se.Name);
                    if (v is StyledElement se2 && se2.Classes.Count > 0) sb.Append(" .").Append(string.Join(".", se2.Classes));
                    if (v is TextBlock tb) sb.Append(" \"").Append(tb.Text).Append('"');
                    sb.Append(v.IsVisible ? "" : " [hidden]").Append($" {v.Bounds}");
                    if (v is Avalonia.Layout.Layoutable lo) sb.Append($" W={lo.Width} HA={lo.HorizontalAlignment} M={lo.Margin} Min={lo.MinWidth}");
                    if (v is StackPanel spn) sb.Append($" Spacing={spn.Spacing}");
                    sb.AppendLine();
                    if (v is not MainWindow && depth < 14) foreach (var c in v.GetVisualChildren()) Walk(c, depth + 1);
                }
                Walk(root, 0);
                File.WriteAllText(dumpPath, sb.ToString());
            }, TimeSpan.FromSeconds(3));
        }
    }

    private StackPanel? _captionPanel;

    private bool PinCaptionButtons()
    {
        if (_captionPanel == null)
        {
            Visual root = this;
            while (root.GetVisualParent() is { } p) root = p;
            _captionPanel = root.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault(s => s.Name == "PART_OverlayPanel");
            if (_captionPanel == null) return false;
        }
        foreach (var child in _captionPanel.Children)
            if (child is Button { Name: "PART_FullScreenButton" } fs && fs.Width != 0)
            {
                // Collapse it completely (zero width, and cancel the panel spacing after it).
                fs.IsVisible = false;
                fs.Width = 0;
                fs.Margin = new Thickness(0, 0, -_captionPanel.Spacing, 0);
                _captionPanel.InvalidateMeasure();
            }
        if (_captionPanel.HorizontalAlignment != Avalonia.Layout.HorizontalAlignment.Right || !double.IsNaN(_captionPanel.Width))
        {
            _captionPanel.Width = double.NaN;
            _captionPanel.MinWidth = 0;
            _captionPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        }
        return true;
    }

    // ---------- IUiServices ----------
    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add files to rename",
            AllowMultiple = true,
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add a folder",
            AllowMultiple = false,
        });
        return folders.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault();
    }

    public async Task<string?> PickCsvAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a rename list",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("CSV or text") { Patterns = ["*.csv", "*.tsv", "*.txt"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault();
    }

    public async Task<string?> SaveCsvAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export the preview",
            SuggestedFileName = suggestedName,
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<bool> ShowMatchFolderAsync(MatchFolderViewModel vm)
    {
        var window = new MatchFolderWindow(vm);
        return await window.ShowDialog<bool>(this);
    }

    public async Task<bool> ShowMatchStyleAsync(MatchStyleViewModel vm)
    {
        var window = new MatchStyleWindow(vm);
        return await window.ShowDialog<bool>(this);
    }

    // ---------- Drag and drop ----------
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool hasFiles = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        ListCard.Classes.Set("dragging", hasFiles);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        ListCard.Classes.Set("dragging", false);
        var items = e.DataTransfer.TryGetFiles();
        if (items == null || Vm == null) return;
        Vm.AddPaths(items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList());
    }

    // ---------- Keyboard ----------
    private void OnFileListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            RemoveSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && Vm != null)
        {
            var selected = FileList.SelectedItems?.OfType<FileItemViewModel>().ToList() ?? [];
            if (selected.Count > 0)
            {
                bool value = !selected[0].IsIncluded;
                foreach (var f in selected) f.IsIncluded = value;
                e.Handled = true;
            }
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm == null) return;
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.O)
        {
            Vm.AddFilesCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Z && FocusManager?.GetFocusedElement() is not TextBox)
        {
            Vm.UndoLastCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Vm.IsHistoryOpen)
        {
            Vm.IsHistoryOpen = false;
            e.Handled = true;
        }
    }

    private void OnRemoveSelected(object? sender, RoutedEventArgs e) => RemoveSelected();

    private void RemoveSelected()
    {
        var selected = FileList.SelectedItems?.OfType<FileItemViewModel>().ToList();
        if (selected is { Count: > 0 }) Vm?.RemoveItems(selected);
    }

    // ---------- Flyouts and tokens ----------
    private void OnFlyoutButtonClick(object? sender, RoutedEventArgs e)
    {
        // Defer, so the button's command runs before the flyout closes and detaches it.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            AddRuleButton.Flyout?.Hide();
            PresetsButton.Flyout?.Hide();
            ClearRulesButton.Flyout?.Hide();
            ListMenuButton.Flyout?.Hide();
        });
    }

    private void OnAnyGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is TextBox tb && tb.Classes.Contains("tokens")) _lastTokenBox = tb;
        else if (e.Source is Visual v && v.FindAncestorOfType<TextBox>() is { } parent && parent.Classes.Contains("tokens")) _lastTokenBox = parent;
    }

    private void OnTokenClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string token }) return;
        var box = _lastTokenBox;
        if (box == null || !box.IsEffectivelyVisible)
        {
            box = this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Classes.Contains("tokens") && t.IsEffectivelyVisible);
        }
        if (box == null) return;
        var text = box.Text ?? string.Empty;
        int caret = Math.Clamp(box.CaretIndex, 0, text.Length);
        box.Text = text.Insert(caret, token);
        box.CaretIndex = caret + token.Length;
        box.Focus();
    }
}
