using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Core;

namespace PyroLauncher.UI.ViewModels;

/// <summary>One color of the active theme in the editor.</summary>
public partial class ThemeColorItem(ThemesViewModel owner, string key, string label) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;

    public string Hex
    {
        get => owner.Theme.Color(Key);
        set
        {
            if (ThemeManager.NormalizeHex(value) is not { } hex || hex == Hex) return;
            owner.SetColor(Key, hex);
            OnPropertyChanged();
        }
    }

    public void Reload() => OnPropertyChanged(nameof(Hex));
}

/// <summary>Settings → Themes: pick, create, edit, rename, delete, import and export themes.</summary>
public partial class ThemesViewModel : ObservableObject
{
    private static readonly FileFilter ThemeFilter = new(L.T("PyroLauncher theme"), "*" + ThemeManager.FileExtension);
    private readonly DispatcherTimer _saveTimer;

    public ObservableCollection<LauncherTheme> Themes => ThemeManager.Themes;
    public LauncherTheme Theme => ThemeManager.Active;
    public List<ThemeColorItem> Colors { get; }

    public ThemesViewModel()
    {
        Colors = LauncherTheme.Keys.Select(k => new ThemeColorItem(this, k.Key, L.T(k.Label))).ToList();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); ThemeManager.Save(Theme); };
    }

    public bool IsEditable => !Theme.IsBuiltIn;
    public bool IsBuiltIn => Theme.IsBuiltIn;

    /// <summary>Font size in percent (80–150).</summary>
    public double FontPercent
    {
        get => Math.Round(Theme.FontScale * 100);
        set
        {
            var scale = Math.Clamp(Math.Round(value / 5) * 5 / 100, LauncherTheme.MinFontScale, LauncherTheme.MaxFontScale);
            if (!IsEditable || Math.Abs(scale - Theme.FontScale) < 0.001) return;
            Theme.FontScale = scale;
            Edited();
        }
    }

    /// <summary>Sidebar (page selector) size in percent (80–150).</summary>
    public double SidebarPercent
    {
        get => Math.Round(Theme.SidebarScale * 100);
        set
        {
            var v = Math.Clamp(Math.Round(value / 5) * 5 / 100, LauncherTheme.MinSize, LauncherTheme.MaxSize);
            if (!IsEditable || Math.Abs(v - Theme.SidebarScale) < 0.001) return;
            Theme.SidebarScale = v;
            Edited();
        }
    }

    /// <summary>Top tab menu size in percent (80–150).</summary>
    public double TopMenuPercent
    {
        get => Math.Round(Theme.TopMenuScale * 100);
        set
        {
            var v = Math.Clamp(Math.Round(value / 5) * 5 / 100, LauncherTheme.MinSize, LauncherTheme.MaxSize);
            if (!IsEditable || Math.Abs(v - Theme.TopMenuScale) < 0.001) return;
            Theme.TopMenuScale = v;
            Edited();
        }
    }

    public string SidebarPercentText => $"{SidebarPercent:0}%";
    public string TopMenuPercentText => $"{TopMenuPercent:0}%";

    /// <summary>Background transparency in percent (0–95).</summary>
    public double TransparencyPercent
    {
        get => Math.Round(Theme.BackgroundTransparency * 100);
        set
        {
            var t = Math.Clamp(Math.Round(value) / 100, 0, LauncherTheme.MaxTransparency);
            if (!IsEditable || Math.Abs(t - Theme.BackgroundTransparency) < 0.001) return;
            Theme.BackgroundTransparency = t;
            Edited();
        }
    }

    public string FontPercentText => $"{FontPercent:0}%";
    public string TransparencyText => $"{TransparencyPercent:0}%";
    public bool HasBackground => Theme.HasBackground;

    internal void SetColor(string key, string hex)
    {
        if (!IsEditable) return;
        Theme.SetColor(key, hex);
        Edited();
    }

    /// <summary>Applies the change live and saves it shortly after.</summary>
    private void Edited()
    {
        ThemeManager.Refresh();
        OnPropertyChanged(nameof(FontPercent));
        OnPropertyChanged(nameof(FontPercentText));
        OnPropertyChanged(nameof(TransparencyPercent));
        OnPropertyChanged(nameof(TransparencyText));
        OnPropertyChanged(nameof(SidebarPercent));
        OnPropertyChanged(nameof(SidebarPercentText));
        OnPropertyChanged(nameof(TopMenuPercent));
        OnPropertyChanged(nameof(TopMenuPercentText));
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Everything bound to the active theme changed (after a switch, create or delete).</summary>
    private void Reload()
    {
        OnPropertyChanged(string.Empty);
        foreach (var c in Colors) c.Reload();
    }

    private void FlushSave()
    {
        if (!_saveTimer.IsEnabled) return;
        _saveTimer.Stop();
        ThemeManager.Save(Theme);
    }

    [RelayCommand]
    private void Switch(LauncherTheme theme)
    {
        if (theme == Theme) return;
        FlushSave();
        ThemeManager.Switch(theme);
        Reload();
    }

    /// <summary>New theme = a copy of the active one, so built-in themes can be customised.</summary>
    [RelayCommand]
    private async Task Create()
    {
        var suggested = ThemeManager.UniqueName(Theme.IsBuiltIn ? L.F("{0} (custom)", Theme.Name) : Theme.Name);
        if (await Dialogs.PromptAsync(L.T("New theme"), L.T("Name"), suggested, L.T("Create")) is not { } name) return;
        FlushSave();
        try
        {
            var theme = ThemeManager.Create(name, Theme);
            ThemeManager.Switch(theme);
            Reload();
        }
        catch (Exception ex) { Dialogs.Toast(L.F("Couldn't create the theme: {0}", ex.Message), true); }
    }

    [RelayCommand]
    private async Task Rename()
    {
        if (!IsEditable) return;
        if (await Dialogs.PromptAsync(L.T("Rename theme"), L.T("Name"), Theme.Name, L.T("Save")) is not { } name || name == Theme.Name) return;
        ThemeManager.Rename(Theme, name);
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (!IsEditable) return;
        var theme = Theme;
        if (!await Dialogs.ConfirmAsync(L.T("Delete theme"), L.F("Delete the theme \"{0}\"? This can't be undone.", theme.Name), L.T("Delete"), true)) return;
        _saveTimer.Stop();
        ThemeManager.Delete(theme);
        Reload();
    }

    [RelayCommand]
    private async Task Import()
    {
        var path = await Ui.Platform.PickFileAsync(L.T("Import theme"),
            [new FileFilter(L.T("Themes"), "*" + ThemeManager.FileExtension, "*.zip", "*.json")]);
        if (path == null) return;
        FlushSave();
        try
        {
            var theme = ThemeManager.Import(path);
            ThemeManager.Switch(theme);
            Reload();
            Dialogs.Toast(L.F("Theme \"{0}\" imported", theme.Name));
        }
        catch (Exception ex) { Dialogs.Toast(L.F("Couldn't import the theme: {0}", ex.Message), true); }
    }

    [RelayCommand]
    private async Task Export()
    {
        var path = await Ui.Platform.SaveFileAsync(L.T("Export theme"), AppPaths.SanitizeFileName(Theme.Name) + ThemeManager.FileExtension, [ThemeFilter]);
        if (path == null) return;
        FlushSave();
        try
        {
            ThemeManager.Export(Theme, path);
            Dialogs.Toast(L.F("Theme exported to {0}", Path.GetFileName(path)));
        }
        catch (Exception ex) { Dialogs.Toast(L.F("Couldn't export the theme: {0}", ex.Message), true); }
    }

    [RelayCommand]
    private async Task ChooseBackground()
    {
        if (!IsEditable) return;
        var path = await Ui.Platform.PickFileAsync(L.T("Choose a background image"),
            [new FileFilter(L.T("Images"), ThemeManager.ImageExtensions.Select(e => "*" + e).ToArray())]);
        if (path == null) return;
        try
        {
            ThemeManager.SetBackground(Theme, path);
            // A solid background would hide the new image completely.
            if (Theme.BackgroundTransparency < 0.05) Theme.BackgroundTransparency = 0.6;
            Edited();
            OnPropertyChanged(nameof(HasBackground));
        }
        catch (Exception ex) { Dialogs.Toast(ex.Message, true); }
    }

    [RelayCommand]
    private void RemoveBackground()
    {
        if (!IsEditable) return;
        ThemeManager.SetBackground(Theme, null);
        Edited();
        OnPropertyChanged(nameof(HasBackground));
    }

    [RelayCommand]
    private void ResetColors()
    {
        if (!IsEditable) return;
        foreach (var k in LauncherTheme.Keys) Theme.SetColor(k.Key, ThemeManager.DefaultColors[k.Key]);
        Theme.FontScale = 1.0;
        Theme.SidebarScale = 1.0;
        Theme.TopMenuScale = 1.0;
        Edited();
        foreach (var c in Colors) c.Reload();
    }
}
