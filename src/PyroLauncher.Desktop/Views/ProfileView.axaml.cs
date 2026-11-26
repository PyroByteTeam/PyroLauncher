using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PyroLauncher.UI.ViewModels;

namespace PyroLauncher.Desktop.Views;

public partial class ProfileView : UserControl
{
    public ProfileView() => InitializeComponent();

    private ProfileViewModel? Vm => DataContext as ProfileViewModel;

    private static SkinItem? ItemOf(object? sender) => (sender as Control)?.DataContext as SkinItem;

    private void Skin_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Vm?.ApplySkinCommand.Execute(item);
    }

    private void SkinApply_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Vm?.ApplySkinCommand.Execute(item);
    }

    private void SkinRename_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Vm?.RenameSkinCommand.Execute(item);
    }

    private void SkinDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Vm?.DeleteSkinCommand.Execute(item);
    }
}
