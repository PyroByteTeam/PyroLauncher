using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PyroLauncher.Accounts;
using PyroLauncher.Core;

namespace PyroLauncher.UI.ViewModels;

public partial class CapeItem : ObservableObject
{
    public CapeInfo? Info { get; init; }
    public string Name => Info?.Alias ?? "No cape";
    [ObservableProperty] private ImageSource? _preview;
    [ObservableProperty] private bool _isActive;
}

/// <summary>Profile page: account switching, 3D skin preview, capes and skin upload (Microsoft accounts).</summary>
public partial class ProfileViewModel : ObservableObject
{
    public AccountsViewModel AccountsVm { get; } = new();

    [ObservableProperty] private Account? _account;
    [ObservableProperty] private BitmapSource? _skin;
    [ObservableProperty] private BitmapSource? _capeTexture;
    [ObservableProperty] private bool _slim;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _pendingSkinPath;
    [ObservableProperty] private bool _uploadSlim;
    public ObservableCollection<CapeItem> Capes { get; } = [];

    public bool IsMicrosoft => Account?.Type == AccountType.Microsoft;
    public bool HasPending => PendingSkinPath != null;
    public string VariantText => Slim ? "Slim (Alex)" : "Classic (Steve)";

    private MinecraftProfile? _profile;
    private CancellationTokenSource? _cts;
    private BitmapSource? _currentSkin;
    private bool _currentSlim;

    public ProfileViewModel()
    {
        AccountManager.SelectionChanged += () => Application.Current.Dispatcher.BeginInvoke(Refresh);
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
                Slim = Controls.SkinViewer.DetectSlim(Skin);
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
            var bmp = await LoadBitmapAsync(url, CancellationToken.None);
            if (bmp == null) return;
            var s = bmp.PixelWidth / 64;
            var crop = new CroppedBitmap(bmp, new Int32Rect(1 * s, 1 * s, 10 * s, 16 * s));
            crop.Freeze();
            item.Preview = crop;
        }
        catch { }
    }

    private static async Task<BitmapSource?> LoadBitmapAsync(string url, CancellationToken ct)
    {
        var bytes = await Http.GetBytesAsync(url, ct);
        return DecodePng(bytes);
    }

    private static BitmapSource? DecodePng(byte[] bytes)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
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
            Dialogs.Toast(item.Info == null ? "Cape hidden" : $"Now wearing {item.Name}");
        }
        catch (Exception ex) { Dialogs.Toast($"Could not change cape: {ex.Message}", true); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void ChooseSkin()
    {
        var dlg = new OpenFileDialog { Title = "Choose a skin", Filter = "Minecraft skin (*.png)|*.png" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var bmp = DecodePng(File.ReadAllBytes(dlg.FileName))!;
            if (bmp.PixelWidth != 64 || (bmp.PixelHeight != 64 && bmp.PixelHeight != 32))
            {
                Dialogs.Toast("Skins must be 64×64 or 64×32 PNG images.", true);
                return;
            }
            UploadSlim = Controls.SkinViewer.DetectSlim(bmp);
            PendingSkinPath = dlg.FileName;
            Skin = bmp;
            Slim = UploadSlim;
        }
        catch (Exception ex) { Dialogs.Toast($"Could not read image: {ex.Message}", true); }
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
            Dialogs.Toast("Skin uploaded — it shows in game after you rejoin.");
        }
        catch (Exception ex) { Dialogs.Toast($"Upload failed: {ex.Message}", true); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ResetSkin()
    {
        if (Account == null || !IsMicrosoft) return;
        if (!await Dialogs.ConfirmAsync("Reset skin", "Reset this account's skin to the default Minecraft skin?", "Reset")) return;
        IsBusy = true;
        try
        {
            _profile = await ProfileApi.ResetSkinAsync(Account, CancellationToken.None);
            await ApplyProfileAsync(_profile, CancellationToken.None);
            _currentSkin = Skin;
            _currentSlim = Slim;
        }
        catch (Exception ex) { Dialogs.Toast($"Reset failed: {ex.Message}", true); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Reload() => Refresh();
}
