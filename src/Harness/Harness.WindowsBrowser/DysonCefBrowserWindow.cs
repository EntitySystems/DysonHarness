using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DysonHarness;

internal sealed class DysonCefBrowserWindow : Window, IDysonBrowserWindow
{
    private static readonly Brush Bg0 = Brush("#0f1115");
    private static readonly Brush Bg1 = Brush("#161a20");
    private static readonly Brush Bg2 = Brush("#1c2129");
    private static readonly Brush Bg3 = Brush("#242b35");
    private static readonly Brush BorderBrushColor = Brush("#2e3642");
    private static readonly Brush TextPrimary = Brush("#d7dde7");
    private static readonly Brush TextMuted = Brush("#8b95a5");
    private static readonly Brush Accent = Brush("#4c8bf5");
    private static readonly Brush AccentSoft = Brush("#1a2a44");
    private static readonly Brush SnipOverlayFill = Brush("#66000000");
    private static readonly Brush SnipSelectionStroke = Brush("#4c8bf5");
    private static readonly Brush SnipSelectionFill = Brush("#334c8bf5");

    private readonly DysonCefBrowserControl _owner;
    private readonly ConcurrentDictionary<string, DysonCefBrowserTab> _tabs = new(StringComparer.Ordinal);
    private readonly StackPanel _tabStrip;
    private readonly TextBox _urlBox;
    private readonly Button _backButton;
    private readonly Button _forwardButton;
    private readonly Button _reloadButton;
    private readonly Button _snipButton;
    private readonly Grid _contentHost;
    private string? _activeTabId;
    private Canvas? _snipOverlay;
    private Rectangle? _snipSelection;
    private Point? _snipOrigin;
    private bool _snipCapturing;

    public DysonCefBrowserWindow(DysonCefBrowserControl owner, string? initialUrl, int width, int height)
    {
        _owner = owner;
        Id = Guid.NewGuid().ToString("N");

        Title = "Dyson Browser";
        Width = width;
        Height = height;
        Background = Bg0;
        Foreground = TextPrimary;
        FontFamily = new FontFamily("IBM Plex Sans, Segoe UI, sans-serif");
        FontSize = 13;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new DockPanel { LastChildFill = true, Background = Bg0 };

        _tabStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Background = Bg1,
            Height = 36,
        };
        var tabRow = new DockPanel { LastChildFill = true, Background = Bg1 };
        DockPanel.SetDock(tabRow, Dock.Top);
        var newTabButton = MakeIconButton("+", "New tab");
        newTabButton.Click += (_, _) => _ = NewTabAsync(null);
        DockPanel.SetDock(newTabButton, Dock.Right);
        tabRow.Children.Add(newTabButton);
        tabRow.Children.Add(_tabStrip);
        root.Children.Add(tabRow);

        var nav = new DockPanel
        {
            LastChildFill = true,
            Background = Bg2,
            Height = 40,
            Margin = new Thickness(0),
        };
        DockPanel.SetDock(nav, Dock.Top);

        _backButton = MakeIconButton("←", "Back");
        _backButton.Click += (_, _) => _ = ActiveTab()?.GoBackAsync();
        _forwardButton = MakeIconButton("→", "Forward");
        _forwardButton.Click += (_, _) => _ = ActiveTab()?.GoForwardAsync();
        _reloadButton = MakeIconButton("↻", "Reload");
        _reloadButton.Click += (_, _) => _ = ActiveTab()?.ReloadAsync();

        var navButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
        };
        navButtons.Children.Add(_backButton);
        navButtons.Children.Add(_forwardButton);
        navButtons.Children.Add(_reloadButton);
        DockPanel.SetDock(navButtons, Dock.Left);
        nav.Children.Add(navButtons);

        _snipButton = MakeIconButton("✂", "Snip page region into agent prompt");
        _snipButton.Click += (_, _) => EnterSnipMode();
        DockPanel.SetDock(_snipButton, Dock.Right);
        nav.Children.Add(_snipButton);

        _urlBox = new TextBox
        {
            Background = Bg3,
            Foreground = TextPrimary,
            BorderBrush = BorderBrushColor,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 8, 6),
            CaretBrush = TextPrimary,
        };
        _urlBox.KeyDown += OnUrlKeyDown;
        nav.Children.Add(_urlBox);
        root.Children.Add(nav);

        PreviewKeyDown += OnWindowPreviewKeyDown;

        _contentHost = new Grid { Background = Bg0 };
        root.Children.Add(_contentHost);
        Content = root;

        Closed += (_, _) =>
        {
            foreach (var tab in _tabs.Values.ToArray())
                tab.DisposeBrowser();
            _tabs.Clear();
            _owner.NotifyWindowClosed(Id);
        };

        _ = NewTabCore(initialUrl);
    }

    public string Id { get; }

    public Task<Result<IReadOnlyList<IDysonBrowserTab>, string>> ListTabsAsync(
        CancellationToken cancellationToken = default) =>
        DysonCefStaHost.InvokeAsync(() =>
        {
            IReadOnlyList<IDysonBrowserTab> list = _tabs.Values.Cast<IDysonBrowserTab>().ToArray();
            return Result<IReadOnlyList<IDysonBrowserTab>, string>.AsValue(list);
        });

    public Task<Result<IDysonBrowserTab, string>> NewTabAsync(
        string? url = null,
        CancellationToken cancellationToken = default) =>
        DysonCefStaHost.InvokeAsync(() =>
        {
            var tab = NewTabCore(url);
            return Result<IDysonBrowserTab, string>.AsValue(tab);
        });

    public Task<VoidResult<string>> CloseTabAsync(
        string tabId,
        CancellationToken cancellationToken = default) =>
        DysonCefStaHost.InvokeAsync(() =>
        {
            if (!_tabs.TryRemove(tabId, out var tab))
                return new VoidResult<string>($"Tab not found: {tabId}");

            tab.DisposeBrowser();
            RemoveTabChip(tabId);

            if (_tabs.Count == 0)
            {
                Close();
                return VoidResult<string>.Success;
            }

            if (string.Equals(_activeTabId, tabId, StringComparison.Ordinal))
                ActivateTabCore(_tabs.Keys.First());

            return VoidResult<string>.Success;
        });

    public Task<VoidResult<string>> ActivateTabAsync(
        string tabId,
        CancellationToken cancellationToken = default) =>
        DysonCefStaHost.InvokeAsync(() =>
        {
            if (!_tabs.ContainsKey(tabId))
                return new VoidResult<string>($"Tab not found: {tabId}");
            ActivateTabCore(tabId);
            return VoidResult<string>.Success;
        });

    public Task<VoidResult<string>> CloseAsync(CancellationToken cancellationToken = default) =>
        DysonCefStaHost.InvokeAsync(() =>
        {
            Close();
            return VoidResult<string>.Success;
        });

    public Task<VoidResult<string>> ResizeAsync(
        int width,
        int height,
        CancellationToken cancellationToken = default) =>
        DysonCefStaHost.InvokeAsync(() =>
        {
            if (width < 200 || height < 200)
                return new VoidResult<string>("Width and height must be at least 200.");
            Width = width;
            Height = height;
            return VoidResult<string>.Success;
        });

    public Task<VoidResult<string>> BringToFrontAsync(CancellationToken cancellationToken = default) =>
        DysonCefStaHost.InvokeAsync(() =>
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
            return VoidResult<string>.Success;
        });

    internal DysonCefBrowserTab? TryGetTab(string tabId) =>
        _tabs.TryGetValue(tabId, out var tab) ? tab : null;

    internal void SyncAddress(string tabId, string? address, string? title)
    {
        if (!string.Equals(_activeTabId, tabId, StringComparison.Ordinal))
            return;

        if (address is not null)
            _urlBox.Text = address;
        if (!string.IsNullOrWhiteSpace(title))
            Title = title + " — Dyson Browser";

        UpdateTabChipTitle(tabId, string.IsNullOrWhiteSpace(title) ? (address ?? "New tab") : title);
    }

    private DysonCefBrowserTab? ActiveTab() =>
        _activeTabId is not null && _tabs.TryGetValue(_activeTabId, out var tab) ? tab : null;

    private DysonCefBrowserTab NewTabCore(string? url)
    {
        var tab = new DysonCefBrowserTab(this, url);
        _tabs[tab.Id] = tab;
        AddTabChip(tab);
        ActivateTabCore(tab.Id);
        return tab;
    }

    private void ActivateTabCore(string tabId)
    {
        ExitSnipMode();
        _activeTabId = tabId;
        _contentHost.Children.Clear();
        if (_tabs.TryGetValue(tabId, out var tab))
        {
            _contentHost.Children.Add(tab.BrowserControl);
            _urlBox.Text = tab.CurrentAddress ?? "";
            Title = string.IsNullOrWhiteSpace(tab.CurrentTitle)
                ? "Dyson Browser"
                : tab.CurrentTitle + " — Dyson Browser";
        }

        RefreshTabChipStyles();
    }

    private void EnterSnipMode()
    {
        if (_snipOverlay is not null || _snipCapturing)
            return;
        if (ActiveTab() is null)
            return;

        var overlay = new Canvas
        {
            Background = SnipOverlayFill,
            Cursor = Cursors.Cross,
            Focusable = true,
        };
        Panel.SetZIndex(overlay, 100);
        overlay.MouseLeftButtonDown += OnSnipMouseDown;
        overlay.MouseMove += OnSnipMouseMove;
        overlay.MouseLeftButtonUp += OnSnipMouseUp;

        var selection = new Rectangle
        {
            Stroke = SnipSelectionStroke,
            StrokeThickness = 1.5,
            Fill = SnipSelectionFill,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        overlay.Children.Add(selection);

        _snipOverlay = overlay;
        _snipSelection = selection;
        _snipOrigin = null;
        _contentHost.Children.Add(overlay);
        overlay.Focus();
    }

    private void ExitSnipMode()
    {
        if (_snipOverlay is null)
            return;

        _contentHost.Children.Remove(_snipOverlay);
        _snipOverlay = null;
        _snipSelection = null;
        _snipOrigin = null;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _snipOverlay is null)
            return;
        e.Handled = true;
        ExitSnipMode();
    }

    private void OnSnipMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_snipOverlay is null || _snipSelection is null)
            return;

        _snipOrigin = e.GetPosition(_snipOverlay);
        _snipSelection.Visibility = Visibility.Visible;
        Canvas.SetLeft(_snipSelection, _snipOrigin.Value.X);
        Canvas.SetTop(_snipSelection, _snipOrigin.Value.Y);
        _snipSelection.Width = 0;
        _snipSelection.Height = 0;
        _snipOverlay.CaptureMouse();
        e.Handled = true;
    }

    private void OnSnipMouseMove(object sender, MouseEventArgs e)
    {
        if (_snipOrigin is null || _snipOverlay is null || _snipSelection is null)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        var pos = e.GetPosition(_snipOverlay);
        var origin = _snipOrigin.Value;
        var x = Math.Min(origin.X, pos.X);
        var y = Math.Min(origin.Y, pos.Y);
        var w = Math.Abs(pos.X - origin.X);
        var h = Math.Abs(pos.Y - origin.Y);
        Canvas.SetLeft(_snipSelection, x);
        Canvas.SetTop(_snipSelection, y);
        _snipSelection.Width = w;
        _snipSelection.Height = h;
        e.Handled = true;
    }

    private void OnSnipMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_snipOverlay is null || _snipSelection is null || _snipOrigin is null)
            return;

        _snipOverlay.ReleaseMouseCapture();
        e.Handled = true;

        var x = Canvas.GetLeft(_snipSelection);
        var y = Canvas.GetTop(_snipSelection);
        var w = _snipSelection.Width;
        var h = _snipSelection.Height;
        _snipOrigin = null;

        // Ignore tiny accidental clicks.
        if (w < 4 || h < 4 || double.IsNaN(x) || double.IsNaN(y))
        {
            ExitSnipMode();
            return;
        }

        _ = CompleteSnipAsync(x, y, w, h);
    }

    private async Task CompleteSnipAsync(double selectionX, double selectionY, double selectionWidth, double selectionHeight)
    {
        if (_snipCapturing)
            return;

        var tab = ActiveTab();
        if (tab is null)
        {
            ExitSnipMode();
            return;
        }

        _snipCapturing = true;
        try
        {
            var contentWidth = _contentHost.ActualWidth;
            var contentHeight = _contentHost.ActualHeight;
            ExitSnipMode();

            var shot = await tab.TakeScreenshotAsync().ConfigureAwait(true);
            if (shot.IsError)
                return;

            var bytes = shot.Value;
            using var input = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(
                input,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];

            var mapped = DysonBrowserSnipCrop.MapDipSelectionToPixelRect(
                selectionX,
                selectionY,
                selectionWidth,
                selectionHeight,
                contentWidth,
                contentHeight,
                frame.PixelWidth,
                frame.PixelHeight);
            if (mapped is null)
                return;

            var (px, py, pw, ph) = mapped.Value;
            var cropped = new CroppedBitmap(frame, new Int32Rect(px, py, pw, ph));
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            using var output = new MemoryStream();
            encoder.Save(output);

            _owner.RaiseSnipCaptured(new DysonBrowserSnipPayload
            {
                ImageBytes = output.ToArray(),
                HtmlRef = "",
                FileName = "browser-snip.jpg",
            });
        }
        catch
        {
            // ponytail: snip is best-effort chrome UX; failures stay silent (no toast bridge yet).
        }
        finally
        {
            _snipCapturing = false;
        }
    }

    private void OnUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        var url = _urlBox.Text?.Trim() ?? "";
        if (url.Length == 0)
            return;
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "https://" + url;
        _ = ActiveTab()?.NavigateAsync(url);
    }

    private void AddTabChip(DysonCefBrowserTab tab)
    {
        var chip = new Border
        {
            Tag = tab.Id,
            Background = AccentSoft,
            BorderBrush = Accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(4, 6, 0, 6),
            Padding = new Thickness(8, 2, 4, 2),
            Cursor = Cursors.Hand,
            Child = BuildChipContent(tab.Id, "New tab"),
        };
        chip.MouseLeftButtonUp += (_, _) => ActivateTabCore(tab.Id);
        _tabStrip.Children.Add(chip);
    }

    private UIElement BuildChipContent(string tabId, string title)
    {
        var row = new DockPanel { LastChildFill = true };
        var close = new Button
        {
            Content = "×",
            Width = 20,
            Height = 20,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = TextMuted,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Tag = tabId,
        };
        close.Click += (_, e) =>
        {
            e.Handled = true;
            _ = CloseTabAsync(tabId);
        };
        DockPanel.SetDock(close, Dock.Right);
        row.Children.Add(close);
        row.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 160,
        });
        return row;
    }

    private void UpdateTabChipTitle(string tabId, string title)
    {
        foreach (var child in _tabStrip.Children.OfType<Border>())
        {
            if (!string.Equals(child.Tag as string, tabId, StringComparison.Ordinal))
                continue;
            child.Child = BuildChipContent(tabId, title);
            break;
        }
    }

    private void RemoveTabChip(string tabId)
    {
        Border? match = null;
        foreach (var child in _tabStrip.Children.OfType<Border>())
        {
            if (string.Equals(child.Tag as string, tabId, StringComparison.Ordinal))
            {
                match = child;
                break;
            }
        }

        if (match is not null)
            _tabStrip.Children.Remove(match);
    }

    private void RefreshTabChipStyles()
    {
        foreach (var child in _tabStrip.Children.OfType<Border>())
        {
            var active = string.Equals(child.Tag as string, _activeTabId, StringComparison.Ordinal);
            child.Background = active ? AccentSoft : Bg3;
            child.BorderBrush = active ? Accent : BorderBrushColor;
        }
    }

    private static Button MakeIconButton(string content, string tooltip)
    {
        return new Button
        {
            Content = content,
            Width = 28,
            Height = 28,
            Margin = new Thickness(2, 0, 2, 0),
            Padding = new Thickness(0),
            Background = Bg3,
            Foreground = TextPrimary,
            BorderBrush = BorderBrushColor,
            BorderThickness = new Thickness(1),
            ToolTip = tooltip,
            Cursor = Cursors.Hand,
        };
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
