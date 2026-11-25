using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PyroLauncher.UI.ViewModels;

namespace PyroLauncher.Desktop.Views;

public partial class ServerPanelView : UserControl
{
    private int _historyIndex = -1;

    public ServerPanelView()
    {
        InitializeComponent();
        CommandBox.AddHandler(KeyDownEvent, OnCommandKey, RoutingStrategies.Tunnel);
    }

    private ServerPanelViewModel? Vm => DataContext as ServerPanelViewModel;

    private void OnCommandKey(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        switch (e.Key)
        {
            case Key.Enter when !CommandBox.IsDropDownOpen:
                vm.SendCommand.Execute(null);
                _historyIndex = -1;
                e.Handled = true;
                break;
            case Key.Tab when CommandBox.IsDropDownOpen || vm.Completions.Count > 0:
                var match = vm.Completions.FirstOrDefault(c => c.StartsWith(vm.Command.TrimStart('/'), StringComparison.OrdinalIgnoreCase));
                if (match != null) SetText(match + " ");
                e.Handled = true;
                break;
            case Key.Up when !CommandBox.IsDropDownOpen:
                if (vm.HistoryAt(_historyIndex + 1) is { } prev) { _historyIndex++; SetText(prev); }
                e.Handled = true;
                break;
            case Key.Down when !CommandBox.IsDropDownOpen:
                if (_historyIndex > 0 && vm.HistoryAt(_historyIndex - 1) is { } next) { _historyIndex--; SetText(next); }
                else { _historyIndex = -1; SetText(""); }
                e.Handled = true;
                break;
        }
    }

    private void SetText(string text)
    {
        CommandBox.Text = text;
        CommandBox.IsDropDownOpen = false;
        if (CommandBox.FindDescendantOfType<TextBox>() is { } box) box.CaretIndex = text.Length;
    }
}
