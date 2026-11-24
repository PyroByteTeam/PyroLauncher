using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Accounts;
using PyroLauncher.Core;

namespace PyroLauncher.UI.ViewModels;

public partial class CapeItem : ObservableObject
{
    public CapeInfo? Info { get; init; }
    public string Name => Info?.Alias ?? L.T("No cape");
    [ObservableProperty] private object? _preview;
    [ObservableProperty] private bool _isActive;
}

/// <summary>Profile page: account switching, 3D skin preview, capes and skin upload (Microsoft accounts).</summary>
public partial class ProfileViewModel : ObservableObject
{
    public AccountsViewModel AccountsVm { get; } = new();

    [ObservableProperty] private Account? _account;
    /// <summary>Skin / cape textures as PNG bytes; the view's skin viewer decodes them.</summary>
    [ObservableProperty] private byte[]? _skin;
    [ObservableProperty] private byte[]? _capeTexture;
    [ObservableProperty] private bool _slim;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _pendingSkinPath;
    [ObservableProperty] private bool _uploadSlim;
    public ObservableCollection<CapeItem> Capes { get; } = [];

    public bool IsMicrosoft => Account?.Type == AccountType.Microsoft;
    public bool HasPending => PendingSkinPath != null;
    public string VariantText => Slim ? L.T("Slim (Alex)") : L.T("Classic (Steve)");

    private MinecraftProfile? _profile;
    private CancellationTokenSource? _cts;
    private byte[]? _currentSkin;
    private bool _currentSlim;

    public ProfileViewModel()
    {
        AccountManager.SelectionChanged += () => Ui.Post(Refresh);
    }

    partial void OnAccountChanged(Account? value) => OnPropertyChanged(nameof(IsMicrosoft));
    partial void OnSlimChanged(bool value)
    {
        OnPropertyChanged(nameof(VariantText));
        if (HasPending && UploadSlim != value) UploadSlim = value;
    }
    partial void OnPendingSkinPathChanged(string? value) => OnPropertyChanged(nameof(HasPending));
    partial void OnUploadSlimChanged(bool value) { if (HasPending) Slim = value; }

    public void Refresh() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Account = AccountManager.Selected;
        PendingSkinPath = null;
        Error = null;
        Capes.Clear();
        _profile = null;
        CapeTexture = null;
        if (Account == null) { Skin = null; return; }

        IsLoading = true;
        try
        {
            if (Account.Type == AccountType.Microsoft)
            {
                _profile = await ProfileApi.GetAsync(Account, cts.Token);
                await ApplyProfileAsync(_profile, cts.Token);
            }
            else
            {
                // Offline accounts have no official skin; preview whatever skin the name has publicly (falls back to Steve).
                Skin = await LoadBitmapAsync($"https://mc-heads.net/skin/{Uri.EscapeDataString(Account.Username)}", cts.Token);
                Slim = DetectSlim(Skin);
            }
            _currentSkin = Skin;
            _currentSlim = Slim;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Warn($"Profile load failed: {ex.Message}");
        }
        finally { if (_cts == cts) IsLoading = false; }
    }

    private async Task ApplyProfileAsync(MinecraftProfile p, CancellationToken ct)
    {
        var skin = p.ActiveSkin;
        Skin = skin != null ? await LoadBitmapAsync(skin.Url, ct) : null;
        Slim = skin?.Slim ?? false;
        CapeTexture = p.ActiveCape != null ? await LoadBitmapAsync(p.ActiveCape.Url, ct) : null;

        Capes.Clear();
        Capes.Add(new CapeItem { Info = null, IsActive = p.ActiveCape == null });
        foreach (var c in p.Capes)
        {
            var item = new CapeItem { Info = c, IsActive = c.Active };
            Capes.Add(item);
            _ = LoadCapePreviewAsync(item, c.Url);
        }
    }

    private static async Task LoadCapePreviewAsync(CapeItem item, string url)
    {
        try
        {
            var png = await LoadBitmapAsync(url, CancellationToken.None);
            if (png == null || Ui.Platform.DecodePixels(png) is not { } px) return;
            var s = Math.Max(1, px.Width / 64);
            item.Preview = Ui.Platform.CropImage(png, 1 * s, 1 * s, 10 * s, 16 * s, Math.Max(1, 8 / s));
        }
        catch { }
    }

    private static async Task<byte[]?> LoadBitmapAsync(string url, CancellationToken ct) => await Http.GetBytesAsync(url, ct);

    /// <summary>Slim (Alex) skins leave the 4th arm column transparent.</summary>
    public static bool DetectSlim(byte[]? png)
    {
        if (png == null || Ui.Platform?.DecodePixels(png) is not { Width: 64, Height: 64 } px) return false;
        for (int y = 20; y < 32; y++)
            for (int x = 54; x < 56; x++)
                if (px.Bgra[(y * 64 + x) * 4 + 3] != 0) return false;
        return true;
    }

    [RelayCommand]
    private async Task SelectCape(CapeItem item)
    {
        if (Account == null || !IsMicrosoft || item.IsActive) return;
        IsBusy = true;
        try
        {
            _profile = await ProfileApi.SetCapeAsync(Account, item.Info?.Id, CancellationToken.None);
            foreach (var c in Capes) c.IsActive = c == item;
            CapeTexture = item.Info != null ? await LoadBitmapAsync(item.Info.Url, CancellationToken.None) : null;
            Dialogs.Toast(item.Info == null ? L.T("Cape hidden") : L.F("Now wearing {0}", item.Name));
        }
        catch (Exception ex) { Dialogs.Toast(L.F("Could not change cape: {0}", ex.Message), true); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ChooseSkin()
    {
        var file = await Ui.Platform.PickFileAsync(L.T("Choose a skin"), [new FileFilter(L.T("Minecraft skin"), "*.png")]);
        if (file == null) return;
        try
        {
            var bmp = File.ReadAllBytes(file);
            if (Ui.Platform.DecodePixels(bmp) is not { Width: 64, Height: 64 or 32 })
            {
                Dialogs.Toast(L.T("Skins must be 64×64 or 64×32 PNG images."), true);
                return;
            }
            UploadSlim = DetectSlim(bmp);
            PendingSkinPath = file;
            Skin = bmp;
            Slim = UploadSlim;
        }
        catch (Exception ex) { Dialogs.Toast(L.F("Could not read image: {0}", ex.Message), true); }
    }

    [RelayCommand]
    private void CancelSkin()
    {
        PendingSkinPath = null;
        Skin = _currentSkin;
        Slim = _currentSlim;
    }

    [RelayCommand]
    private async Task UploadSkin()
    {
        if (Account == null || PendingSkinPath == null) return;
        IsBusy = true;
        try
        {
            _profile = await ProfileApi.UploadSkinAsync(Account, PendingSkinPath, UploadSlim, CancellationToken.None);
            PendingSkinPath = null;
            await ApplyProfileAsync(_profile, CancellationToken.None);
            _currentSkin = Skin;
            _currentSlim = Slim;
            Dialogs.Toast(L.T("Skin uploaded - it shows in game after you rejoin."));
        }
        catch (Exception ex) { Dialogs.Toast(L.F("Upload failed: {0}", ex.Message), true); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ResetSkin()
    {
        if (Account == null || !IsMicrosoft) return;
        if (!await Dialogs.ConfirmAsync(L.T("Reset skin"), L.T("Reset this account's skin to the default Minecraft skin?"), L.T("Reset"))) return;
        IsBusy = true;
        try
        {
            _profile = await ProfileApi.ResetSkinAsync(Account, CancellationToken.None);
            await ApplyProfileAsync(_profile, CancellationToken.None);
            _currentSkin = Skin;
            _currentSlim = Slim;
        }
        catch (Exception ex) { Dialogs.Toast(L.F("Reset failed: {0}", ex.Message), true); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Reload() => Refresh();
}
