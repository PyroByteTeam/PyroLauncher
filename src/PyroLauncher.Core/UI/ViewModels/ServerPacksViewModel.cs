using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PyroLauncher.UI.ViewModels;

/// <summary>Placeholder for the upcoming server pack manager.</summary>
public partial class ServerPacksViewModel : ObservableObject
{
    [RelayCommand] private void OpenDiscover() => MainViewModel.Instance.Nav = "discover";
}
