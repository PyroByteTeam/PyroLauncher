using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using PyroLauncher.Core;
using PyroLauncher.UI;

namespace PyroLauncher.Desktop.Views;

public partial class ImageViewerView : UserControl
{
    private ImageViewerViewModel? _vm;

    public ImageViewerView()
    {
        InitializeComponent();
        // Take keyboard focus so the arrow keys switch images right away.
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnChanged;
            _vm = DataContext as ImageViewerViewModel;
            if (_vm != null) _vm.PropertyChanged += OnChanged;
            LoadLocal();
        };
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImageViewerViewModel.LocalUri)) LoadLocal();
    }

    // Local screenshots load at full size from disk.
    private void LoadLocal() =>
        Local.Source = _vm?.LocalUri is { } path ? Ui.LoadImageFile(path, 0) as IImage : null;
}
