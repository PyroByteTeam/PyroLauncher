using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace PyroLauncher.Desktop;

/// <summary>Shows a view model with the view of the same name: HomeViewModel → Views.HomeView.</summary>
public sealed class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Type?> Cache = [];

    private static Type? ViewFor(Type vm)
    {
        if (Cache.TryGetValue(vm, out var t)) return t;
        var name = vm.Name.EndsWith("ViewModel") ? vm.Name[..^"ViewModel".Length] + "View" : null;
        t = name == null ? null : typeof(ViewLocator).Assembly.GetType($"PyroLauncher.Desktop.Views.{name}");
        Cache[vm] = t;
        return t;
    }

    public bool Match(object? data) => data != null && ViewFor(data.GetType()) != null;

    public Control? Build(object? data) =>
        data != null && ViewFor(data.GetType()) is { } t ? (Control)Activator.CreateInstance(t)! : new TextBlock { Text = data?.GetType().Name };
}
