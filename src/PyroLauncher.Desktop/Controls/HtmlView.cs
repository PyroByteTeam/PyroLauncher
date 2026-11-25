using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PyroLauncher.Core;
using TheArtOfDev.HtmlRenderer.Avalonia;

namespace PyroLauncher.Desktop.Controls;

/// <summary>Renders an HTML fragment (mod descriptions, changelogs) with dark styling. Links open in the browser.</summary>
public class HtmlView : UserControl
{
    public static readonly StyledProperty<string?> HtmlProperty = AvaloniaProperty.Register<HtmlView, string?>(nameof(Html));
    public string? Html { get => GetValue(HtmlProperty); set => SetValue(HtmlProperty, value); }

    private const string Css = """
        body { color:#d8d8d8; font-family:Inter, 'Segoe UI', sans-serif; font-size:10pt; line-height:1.5; margin:0; padding:0 8px 20px 2px; background-color:transparent; }
        a { color:#ffffff; text-decoration:underline; }
        h1, h2, h3, h4 { color:#ffffff; font-weight:bold; margin:14px 0 6px 0; }
        h1 { font-size:16pt; } h2 { font-size:14pt; } h3 { font-size:12pt; }
        p { margin:0 0 10px 0; }
        p, li, ul, ol, div, span, em, strong, b, i, td, th, dd, dt, small, center, font { color:#d8d8d8; }
        img { max-width:100%; }
        code { background-color:#222222; font-family:Consolas, Menlo, monospace; font-size:9pt; }
        pre { background-color:#1e1e1e; padding:8px; }
        blockquote { border-left:3px solid #444444; margin:0; padding:2px 12px; color:#aaaaaa; }
        table { border-collapse:collapse; } td, th { border:1px solid #333333; padding:4px 8px; }
        hr { border:0; border-top:1px solid #333333; }
        .empty { color:#777777; font-style:italic; }
        """;

    private readonly HtmlPanel _panel;

    public HtmlView()
    {
        _panel = new HtmlPanel { Background = Brushes.Transparent, BaseStylesheet = Css };
        _panel.LinkClicked += (_, e) =>
        {
            e.Event.Handled = true;
            var link = e.Event.Link;
            if (link.StartsWith("http", StringComparison.OrdinalIgnoreCase)) Ui.OpenUrl(link);
        };
        Content = _panel;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HtmlProperty)
            _panel.Text = string.IsNullOrWhiteSpace(Html) ? "<p class='empty'>Nothing here.</p>" : Html;
    }
}
