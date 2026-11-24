using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Accounts;
using PyroLauncher.Core;
using PyroLauncher.Instances;

namespace PyroLauncher.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public static MainViewModel Instance { get; private set; } = null!;

    public HomeViewModel Home { get; }
    private SettingsViewModel? _settings;
    private DownloadsViewModel? _downloads;
    private ProfileViewModel? _profile;
    private InstanceViewModel? _lastInstance;

    [ObservableProperty] private object _currentPage;
    [ObservableProperty] private object? _dialog;
    [ObservableProperty] private string _nav = "home";
    [ObservableProperty] private Account? _account;

    public System.Collections.ObjectModel.ObservableCollection<ToastItem> Toasts => Dialogs.Toasts;
    public DownloadSummary Downloads => DownloadManager.Summary;

    public MainViewModel()
    {
        Instance = this;
        Home = new HomeViewModel();
        _currentPage = Home;
        Account = AccountManager.Selected;
        AccountManager.SelectionChanged += () => Account = AccountManager.Selected;
    }

    partial void OnNavChanged(string value)
    {
        switch (value)
        {
            case "home":
                if (CurrentPage is not HomeViewModel and not InstanceViewModel) CurrentPage = (object?)_lastInstance ?? Home;
                break;
            case "settings": CurrentPage = _settings ??= new SettingsViewModel(); break;
            case "downloads": CurrentPage = _downloads ??= new DownloadsViewModel(); break;
            case "profile":
                _profile ??= new ProfileViewModel();
                _profile.Refresh();
                CurrentPage = _profile;
                break;
        }
    }

    [RelayCommand]
    private void GoHome()
    {
        _lastInstance?.Detach();
        _lastInstance = null;
        if (Nav != "home") Nav = "home";
        CurrentPage = Home;
    }

    [RelayCommand] private void GoSettings() => Nav = "settings";
    [RelayCommand] private void GoDownloads() => Nav = "downloads";
    [RelayCommand] private void OpenAccounts() => Nav = "profile";
    [RelayCommand] private void DismissToast(ToastItem t) => Dialogs.Toasts.Remove(t);

    public void OpenInstance(Instance inst, string? tab = null)
    {
        if (CurrentPage is InstanceViewModel cur && cur.Instance == inst)
        {
            if (tab != null) cur.Tab = tab;
            return;
        }
        _lastInstance?.Detach();
        var vm = new InstanceViewModel(inst);
        if (tab != null) vm.Tab = tab;
        _lastInstance = vm;
        CurrentPage = vm;
        Nav = "home";
    }
}
