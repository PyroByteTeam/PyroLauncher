using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace PyroLauncher.Desktop.Views;

public partial class PromptDialogView : UserControl
{
    public PromptDialogView() => InitializeComponent();

    private void Input_Attached(object? sender, VisualTreeAttachmentEventArgs e) =>
        Dispatcher.UIThread.Post(() => { Input.Focus(); Input.SelectAll(); }, DispatcherPriority.Loaded);
}
