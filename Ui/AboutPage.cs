using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

// The About page: logo, name, version, author and license, and an update
// check that runs every time the page is shown. A found update is reported
// back so the window can show it in the title and on the nav icon.
sealed class AboutPage : UserControl
{
    readonly TextBlock _status = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
    readonly TextBlock _download = new() { HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
    readonly Action<Version> _updateFound;
    CancellationTokenSource? _checkCts;

    public AboutPage(Action<Version> updateFound)
    {
        _updateFound = updateFound;

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
        stack.Children.Add(Logo(52));
        stack.Children.Add(new TextBlock
        {
            Text = "PC MQTT Monitor", Style = (Style)Application.Current.FindResource("PageTitle"),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 2),
        });
        stack.Children.Add(Secondary(string.Format(L.T.AboutVersion, UpdateChecker.CurrentVersion.ToString(3)), 14));
        stack.Children.Add(_status);
        _download.Inlines.Add(Link(L.T.AboutDownload, TrayApp.OpenReleasesPage));
        stack.Children.Add(_download);

        var repo = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 6) };
        string repoUrl = $"https://github.com/{UpdateChecker.RepoOwner}/{UpdateChecker.RepoName}";
        repo.Inlines.Add(Link($"github.com/{UpdateChecker.RepoOwner}/{UpdateChecker.RepoName}", () => OpenUrl(repoUrl)));
        stack.Children.Add(repo);
        stack.Children.Add(Secondary($"© {DateTime.Now.Year} Marcus Voß — {L.T.AboutLicense}", 12));
        stack.Children.Add(Secondary(L.T.AboutDeps, 12));
        Content = stack;

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) RunUpdateCheck();
            else _checkCts?.Cancel();
        };
    }

    static TextBlock Secondary(string text, double size)
    {
        var tb = new TextBlock { Text = text, FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return tb;
    }

    static Hyperlink Link(string text, Action click)
    {
        var link = new Hyperlink(new Run(text));
        link.Click += (_, _) => click();
        return link;
    }

    static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Write($"[ui] open url failed: {ex.Message}"); }
    }

    // The app's signal-bars logo (same design as app.ico) in the accent color.
    internal static FrameworkElement Logo(double size)
    {
        double u = size / 16;
        var canvas = new Canvas { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (x, y, opacity) in new[] { (1.0, 10.0, 1.0), (5.0, 7.0, 1.0), (9.0, 4.0, 1.0), (13.0, 1.0, 0.45) })
        {
            var bar = new Rectangle { Width = 2.6 * u, Height = (15 - y) * u, RadiusX = 0.8 * u, RadiusY = 0.8 * u, Opacity = opacity };
            bar.SetResourceReference(Shape.FillProperty, "AccentFillColorDefaultBrush");
            Canvas.SetLeft(bar, x * u);
            Canvas.SetTop(bar, y * u);
            canvas.Children.Add(bar);
        }
        return canvas;
    }

    async void RunUpdateCheck()
    {
        _checkCts?.Cancel();
        var cts = _checkCts = new CancellationTokenSource();
        SetStatus(L.T.AboutChecking, "TextFillColorTertiaryBrush");
        _download.Visibility = Visibility.Collapsed;
        try
        {
            var newer = await UpdateChecker.CheckAsync(cts.Token);
            if (cts.Token.IsCancellationRequested) return;
            if (newer != null)
            {
                SetStatus(string.Format(L.T.AboutUpdateFound, newer.ToString(3)), "TextFillColorPrimaryBrush");
                _download.Visibility = Visibility.Visible;
                _updateFound(newer);
            }
            else SetStatus(L.T.AboutUpToDate, "SystemFillColorSuccessBrush");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus(string.Format(L.T.AboutCheckFailed, ex.Message), "TextFillColorTertiaryBrush"); }
    }

    void SetStatus(string text, string brush)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }
}
