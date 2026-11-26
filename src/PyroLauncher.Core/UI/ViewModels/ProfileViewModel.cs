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

/// <summary>A skin in the local skin library.</summary>
public partial class SkinItem(SavedSkin model) : ObservableObject
{
    public SavedSkin Model { get; } = model;
    [ObservableProperty] private string _name = model.Name;
    [ObservableProperty] private object? _preview;
    /// <summary>The account is wearing this skin right now.</summary>
    [ObservableProperty] private bool _isActive;
    /// <summary>Shown in the preview, waiting to be applied.</summary>
    [ObservableProperty] private bool _isPending;
    public string VariantText => Model.Slim ? L.T("Slim") : L.T("Classic");
    public void NotifyVariant() => OnPropertyChanged(nameof(VariantText));
}

/// <summary>Profile page: account switching, 3D skin preview, capes, skin upload and a skin library (Microsoft accounts).</summary>
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
    public ObservableCollection<SkinItem> Library { get; } = [];
    /// <summary>The library skin being previewed (null when the pending skin came from a file).</summary>
    [ObservableProperty] private SkinItem? _pendingLibrarySkin;

    public bool IsMicrosoft => Account?.Type == AccountType.Microsoft;
    public bool HasPending => PendingSkinPath != null;
    public bool HasLibrary => Library.Count > 0;
    public bool PendingFromFile => HasPending && PendingLibrarySkin == null;
    public string? PendingLabel => PendingLibrarySkin?.Name ?? PendingSkinPath;
    public string VariantText => Slim ? L.T("Slim (Alex)") : L.T("Classic (Steve)");

    private MinecraftProfile? _profile;
    private CancellationTokenSource? _cts;
    private byte[]? _currentSkin;
    private bool _currentSlim;

    public ProfileViewModel()
    {
        AccountManager.SelectionChanged += () => Ui.Post(Refresh);
        Library.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLibrary));
        LoadLibrary();
    }

    private void LoadLibrary()
    {
        Library.Clear();
        foreach (var s in SkinLibrary.Load().OrderBy(s => s.Added)) Library.Add(CreateItem(s));
    }

    private static SkinItem CreateItem(SavedSkin s)
    {
        var item = new SkinItem(s);
        _ = Task.Run(() =>
        {
            try
            {
                var png = File.ReadAllBytes(s.FilePath);
                var px = SkinRender.Front(png, s.Slim);
                if (px != null) Ui.Post(() => item.Preview = Ui.Platform.ImageFromPixels(px.Value.Width, px.Value.Height, px.Value.Bgra));
            }
            catch (Exception ex) { Log.Warn($"Skin preview failed: {ex.Message}"); }
        });
        return item;
    }

    private void UpdateLibraryState()
    {
        var url = _profile?.ActiveSkin?.Url;
        foreach (var s in Library)
        {
            s.IsActive = url != null && s.Model.TextureUrl == url;
            s.IsPending = s == PendingLibrarySkin;
        }
    }

    partial void OnPendingLibrarySkinChanged(SkinItem? value)
    {
        UpdateLibraryState();
        OnPropertyChanged(nameof(PendingFromFile));
        OnPropertyChanged(nameof(PendingLabel));
    }

    partial void OnAccountChanged(Account? value) => OnPropertyChanged(nameof(IsMicrosoft));
    partial void OnSlimChanged(bool value)
    {
        OnPropertyChanged(nameof(VariantText));
        if (HasPending && UploadSlim != value) UploadSlim = value;
    }
    partial void OnPendingSkinPathChanged(string? value)
    {
        if (value == null) PendingLibrarySkin = null;
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(PendingFromFile));
        OnPropertyChanged(nameof(PendingLabel));
    }
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
        UpdateLibraryState();
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
            PendingLibrarySkin = null;
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
        var fromLibrary = PendingLibrarySkin;
        var slim = UploadSlim;
        IsBusy = true;
        try
        {
            _profile = await ProfileApi.UploadSkinAsync(Account, PendingSkinPath, slim, CancellationToken.None);
            PendingSkinPath = null;
            if (fromLibrary != null)
            {
                // Remember the texture Mojang assigned so the library can tell which skin is being worn.
                fromLibrary.Model.TextureUrl = _profile.ActiveSkin?.Url;
                fromLibrary.Model.Slim = slim;
                fromLibrary.NotifyVariant();
                SkinLibrary.Store(fromLibrary.Model);
            }
            await ApplyProfileAsync(_profile, CancellationToken.None);
            _currentSkin = Skin;
            _currentSlim = Slim;
            Dialogs.Toast(fromLibrary != null
                ? L.F("Now wearing {0} - it shows in game after you rejoin.", fromLibrary.Name)
                : L.T("Skin uploaded - it shows in game after you rejoin."));
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

    // ---------- skin library ----------

    private static bool IsValidSkin(byte[] png) => Ui.Platform.DecodePixels(png) is { Width: 64, Height: 64 or 32 };

    private SkinItem AddToLibrary(string name, byte[] png, bool slim, string? textureUrl)
    {
        var item = CreateItem(SkinLibrary.Add(name, png, slim, textureUrl));
        Library.Add(item);
        UpdateLibraryState();
        return item;
    }

    /// <summary>Saves the skin the account is wearing right now.</summary>
    [RelayCommand]
    private async Task SaveCurrentSkin()
    {
        if (_currentSkin == null || !IsValidSkin(_currentSkin)) { Dialogs.Toast(L.T("There is no skin to save yet."), true); return; }
        var url = _profile?.ActiveSkin?.Url;
        if (url != null && Library.FirstOrDefault(s => s.Model.TextureUrl == url) is { } existing)
        {
            Dialogs.Toast(L.F("This skin is already in your library as {0}.", existing.Name));
            return;
        }
        var name = await Dialogs.PromptAsync(L.T("Save skin"), L.T("Name"), Account?.Username ?? L.T("My skin"));
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            AddToLibrary(name, _currentSkin, _currentSlim, url);
            Dialogs.Toast(L.F("Saved {0} to your skins.", name.Trim()));
        }
        catch (Exception ex) { Dialogs.Toast(L.F("Could not save skin: {0}", ex.Message), true); }
    }

    /// <summary>Adds one or more PNG files to the library.</summary>
    [RelayCommand]
    private async Task AddSkinFiles()
    {
        var files = await Ui.Platform.PickFilesAsync(L.T("Add skins"), [new FileFilter(L.T("Minecraft skin"), "*.png")]);
        SkinItem? last = null;
        int bad = 0;
        foreach (var file in files)
        {
            try
            {
                var png = File.ReadAllBytes(file);
                if (!IsValidSkin(png)) { bad++; continue; }
                var name = files.Count == 1
                    ? await Dialogs.PromptAsync(L.T("Add skin"), L.T("Name"), Path.GetFileNameWithoutExtension(file))
                    : Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(name)) continue;
                last = AddToLibrary(name, png, DetectSlim(png), null);
            }
            catch (Exception ex) { Dialogs.Toast(L.F("Could not read image: {0}", ex.Message), true); }
        }
        if (bad > 0) Dialogs.Toast(L.T("Skins must be 64×64 or 64×32 PNG images."), true);
        if (last != null) PreviewSkin(last);
    }

    /// <summary>Saves the skin file that is being previewed (picked with "Choose skin file") to the library.</summary>
    [RelayCommand]
    private async Task SavePendingToLibrary()
    {
        if (PendingSkinPath == null || PendingLibrarySkin != null) return;
        var name = await Dialogs.PromptAsync(L.T("Save skin"), L.T("Name"), Path.GetFileNameWithoutExtension(PendingSkinPath));
        if (string.IsNullOrWhiteSpace(name) || PendingSkinPath == null) return;
        try
        {
            var item = AddToLibrary(name, File.ReadAllBytes(PendingSkinPath), UploadSlim, null);
            PreviewSkin(item);
        }
        catch (Exception ex) { Dialogs.Toast(L.F("Could not save skin: {0}", ex.Message), true); }
    }

    /// <summary>Shows a library skin in the preview, ready to apply.</summary>
    [RelayCommand]
    private void PreviewSkin(SkinItem item)
    {
        if (item == PendingLibrarySkin) return;
        try
        {
            var png = File.ReadAllBytes(item.Model.FilePath);
            PendingLibrarySkin = item;
            UploadSlim = item.Model.Slim;
            PendingSkinPath = item.Model.FilePath;
            Skin = png;
            Slim = item.Model.Slim;
            OnPropertyChanged(nameof(PendingLabel));
        }
        catch (Exception ex) { Dialogs.Toast(L.F("Could not read image: {0}", ex.Message), true); }
    }

    [RelayCommand]
    private async Task ApplySkin(SkinItem item)
    {
        if (!IsMicrosoft || item.IsActive) return;
        if (PendingLibrarySkin != item) PreviewSkin(item);
        if (PendingLibrarySkin == item) await UploadSkin();
    }

    [RelayCommand]
    private async Task RenameSkin(SkinItem item)
    {
        var name = await Dialogs.PromptAsync(L.T("Rename skin"), L.T("Name"), item.Name, L.T("Rename"));
        if (string.IsNullOrWhiteSpace(name)) return;
        item.Model.Name = item.Name = name.Trim();
        SkinLibrary.Store(item.Model);
        if (item == PendingLibrarySkin) OnPropertyChanged(nameof(PendingLabel));
    }

    [RelayCommand]
    private async Task DeleteSkin(SkinItem item)
    {
        if (!await Dialogs.ConfirmAsync(L.T("Delete skin"), L.F("Remove {0} from your skin library? This doesn't change the skin on your account.", item.Name), L.T("Delete"), true)) return;
        if (item == PendingLibrarySkin) CancelSkin();
        SkinLibrary.Remove(item.Model);
        Library.Remove(item);
    }

    [RelayCommand]
    private void OpenSkinsFolder()
    {
        Directory.CreateDirectory(AppPaths.Skins);
        Dialogs.OpenPath(AppPaths.Skins);
    }
}
