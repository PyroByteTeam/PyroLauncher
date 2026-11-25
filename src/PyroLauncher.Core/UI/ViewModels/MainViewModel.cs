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
    private DiscoverViewModel? _discover;
    private PresetsViewModel? _presets;
    private ServerPacksViewModel? _serverPacks;
    private ModpackPageViewModel? _modpackPage;
    private InstanceViewModel? _lastInstance;

    [ObservableProperty] private object _currentPage;
    [ObservableProperty] private object? _dialog;
    [ObservableProperty] private string _nav = "home";
    [ObservableProperty] private Account? _account;
    public string AccountLabel => Account?.Username ?? L.T("No account");
    partial void OnAccountChanged(Account? value) => OnPropertyChanged(nameof(AccountLabel));

    public System.Collections.ObjectModel.ObservableCollection<ToastItem> Toasts => Dialogs.Toasts;
    public DownloadSummary Downloads => DownloadManager.Summary;

    public MainViewModel()
    {
        Instance = this;
        Home = new HomeViewModel();
        _currentPage = Home;
        Account = AccountManager.Selected;
        AccountManager.SelectionChanged += () => Account = AccountManager.Selected;
        Updater.AvailableChanged += () => Ui.Post(() =>
        {
            OnPropertyChanged(nameof(UpdateAvailable));
            OnPropertyChanged(nameof(UpdateTip));
        });
    }

    public bool UpdateAvailable => Updater.Available != null;
    public string UpdateTip => Updater.Available is { } u ? L.F("Update to {0}", u.Version.ToString(3)) : "";
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private double _updateProgress;

    /// <summary>Checks GitHub once when the launcher starts; silent when offline.</summary>
    private bool _startupCheckDone;

    public async void CheckForUpdatesOnStartup()
    {
        if (_startupCheckDone) return;
        _startupCheckDone = true;
        try { await Updater.CheckAsync(); }
        catch (Exception ex) { Log.Info("Update check skipped: " + ex.Message); }
    }

    [RelayCommand]
    public async Task UpdateLauncher()
    {
        if (Updater.Available is not { } info || IsUpdating) return;
        var version = info.Version.ToString(3);
        var auto = L.T("Update automatically");
        var manual = L.T("Download manually");
        string? choice;
        if (Updater.AutoUpdateUrl(info) != null)
            choice = await Dialogs.ChooseAsync(L.F("Update to {0}", version),
                L.F("PyroLauncher {0} is available (you have {1}). The launcher can download it, close, replace itself and start again. If you installed it in an unusual way, download the update yourself instead.",
                    version, Updater.Current.ToString(3)),
                [auto, manual]);
        else
            choice = await Dialogs.ConfirmAsync(L.F("Update to {0}", version),
                L.F("PyroLauncher {0} is available (you have {1}). This copy was installed by your package manager or in a way the launcher can't update itself, so download the new version from the releases page.",
                    version, Updater.Current.ToString(3)), L.T("Open releases page")) ? manual : null;

        if (choice == manual) { Ui.OpenUrl(info.PageUrl); return; }
        if (choice != auto) return;

        IsUpdating = true;
        UpdateProgress = 0;
        try
        {
            await Updater.PrepareAsync(info, new Progress<double>(p => UpdateProgress = p));
            Ui.Platform.MainWindow(WindowAction.Quit);
        }
        catch (Exception ex)
        {
            Log.Error("Launcher update failed", ex);
            Dialogs.Toast(L.F("Update failed: {0}", ex.Message), true);
            IsUpdating = false;
        }
    }

    partial void OnNavChanged(string value)
    {
        switch (value)
        {
            case "home":
                if (CurrentPage is not HomeViewModel and not InstanceViewModel) CurrentPage = (object?)_lastInstance ?? Home;
                break;
            case "serverpacks": CurrentPage = _serverPacks ??= new ServerPacksViewModel(); break;
            case "presets": CurrentPage = _presets ??= new PresetsViewModel(); break;
            case "discover": CurrentPage = (object?)_modpackPage ?? (_discover ??= new DiscoverViewModel()); break;
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

    [RelayCommand] private void GoDiscover() => Nav = "discover";
    [RelayCommand] private void GoSettings() => Nav = "settings";

    /// <summary>Shows a modpack page inside the Discover section.</summary>
    public void OpenModpack(ModpackPageViewModel vm)
    {
        _modpackPage = vm;
        if (Nav != "discover") Nav = "discover";
        CurrentPage = vm;
    }

    /// <summary>Presets page, ready to apply a preset to <paramref name="inst"/>.</summary>
    public void OpenPresetsApply(Instances.Instance inst, Content.ContentPreset? preset)
    {
        _presets ??= new PresetsViewModel();
        if (Nav != "presets") Nav = "presets";
        CurrentPage = _presets;
        _presets.StartApply(inst, preset);
    }

    /// <summary>Back from a modpack page to the search results.</summary>
    public void ShowDiscover()
    {
        _modpackPage = null;
        if (Nav != "discover") Nav = "discover";
        CurrentPage = _discover ??= new DiscoverViewModel();
    }

    /// <summary>Rebuilds pages so texts made in code pick up the new language.</summary>
    public void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(AccountLabel));
        _downloads = null;
        _discover = null;
        _modpackPage = null;
        _profile = null;
        _presets = null;
        foreach (var i in InstanceManager.Instances) i.Refresh();
        Home.Rebuild();
        if (_lastInstance != null)
        {
            var inst = _lastInstance.Instance;
            _lastInstance.Detach();
            _lastInstance = new InstanceViewModel(inst);
        }
        _settings = new SettingsViewModel { Section = "appearance" };
        if (Nav == "settings") CurrentPage = _settings;
    }
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
