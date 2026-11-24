using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PyroLauncher.Core;

namespace PyroLauncher.UI.Controls;

/// <summary>Renders an HTML fragment (mod descriptions, changelogs) in a dark-styled WebView2. Links open in the browser.</summary>
public class HtmlView : ContentControl
{
    private static Task<CoreWebView2Environment>? _env;
    private readonly WebView2 _web;
    private bool _ready;

    public static readonly DependencyProperty HtmlProperty = DependencyProperty.Register(
        nameof(Html), typeof(string), typeof(HtmlView), new PropertyMetadata(null, (d, _) => ((HtmlView)d).Render()));

    public string? Html
    {
        get => (string?)GetValue(HtmlProperty);
        set => SetValue(HtmlProperty, value);
    }

    public HtmlView()
    {
        _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 21, 21, 21) };
        Content = _web;
        Loaded += OnLoaded;
        Unloaded += (_, _) => Dialogs.OverlayChanged -= OnOverlayChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Dialogs.OverlayChanged -= OnOverlayChanged;
        Dialogs.OverlayChanged += OnOverlayChanged;
        OnOverlayChanged(Dialogs.IsOverlayOpen);
        if (_ready) return;
        try
        {
            _env ??= CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData);
            await _web.EnsureCoreWebView2Async(await _env);
            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += (_, args) =>
            {
                if (args.Uri.StartsWith("data:") || args.Uri.StartsWith("about:")) return;
                args.Cancel = true;
                OpenExternal(args.Uri);
            };
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                OpenExternal(args.Uri);
            };
            _ready = true;
            Render();
        }
        catch (Exception ex)
        {
            Log.Error("WebView2 init failed", ex);
            Content = new TextBlock
            {
                Text = "Microsoft Edge WebView2 Runtime is required to display descriptions.",
                Foreground = (System.Windows.Media.Brush)FindResource("Fg2"),
                Margin = new Thickness(12),
                TextWrapping = TextWrapping.Wrap,
            };
        }
    }

    // WebView2 is a native window and always draws above WPF content, so hide it while a dialog is open.
    private void OnOverlayChanged(bool open) => _web.Visibility = open ? Visibility.Hidden : Visibility.Visible;

    private static void OpenExternal(string uri)
    {
        if (!uri.StartsWith("http://") && !uri.StartsWith("https://")) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    private void Render()
    {
        if (!_ready) return;
        var doc = $$"""
            <!DOCTYPE html><html><head><meta charset="utf-8"><base target="_blank">
            <style>
              html,body{background:#151515;color:#d8d8d8;font-family:'Segoe UI Variable Text','Segoe UI',sans-serif;font-size:14px;line-height:1.55;margin:0;padding:0 6px 24px 2px}
              a{color:#fff;text-decoration:underline;text-decoration-color:#666}
              h1,h2,h3,h4{color:#fff;font-weight:600;margin:1.2em 0 .5em}
              h1{font-size:1.6em}h2{font-size:1.35em}h3{font-size:1.15em}
              img,video,iframe{max-width:100%;height:auto;border-radius:6px}
              code{background:#222;padding:1px 5px;border-radius:4px;font-family:'Cascadia Mono',Consolas,monospace;font-size:.9em}
              pre{background:#1e1e1e;padding:10px;border-radius:8px;overflow:auto}pre code{background:none;padding:0}
              blockquote{border-left:3px solid #444;margin:0;padding:2px 12px;color:#aaa}
              table{border-collapse:collapse}td,th{border:1px solid #333;padding:4px 8px}
              hr{border:none;border-top:1px solid #333}
              ::-webkit-scrollbar{width:10px}::-webkit-scrollbar-thumb{background:#3a3a3a;border-radius:5px;border:2px solid #151515}
              .empty{color:#777;font-style:italic}
            </style></head><body>{{(string.IsNullOrWhiteSpace(Html) ? "<p class='empty'>Nothing here.</p>" : Html)}}</body></html>
            """;
        try { _web.NavigateToString(doc); }
        catch (Exception ex) { Log.Warn($"HtmlView render failed: {ex.Message}"); }
    }
}
