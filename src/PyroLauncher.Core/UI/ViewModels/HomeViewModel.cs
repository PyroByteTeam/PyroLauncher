using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Core;
using PyroLauncher.Instances;

namespace PyroLauncher.UI.ViewModels;

public partial class CategoryGroup : ObservableObject
{
    public string Name { get; init; } = "";
    /// <summary>null for the "Uncategorized" group.</summary>
    public string? Key { get; init; }
    public bool IsUser => Key != null;
    public ObservableCollection<Instance> Items { get; } = [];
    [ObservableProperty] private bool _isExpanded = true;
    public string CountText => Items.Count == 1 ? L.T("1 instance") : L.F("{0} instances", Items.Count);
}

public partial class HomeViewModel : ObservableObject
{
    public ObservableCollection<CategoryGroup> Groups { get; } = [];
    private readonly Dictionary<string, bool> _expanded = [];

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _isEmpty;

    [ObservableProperty] private string _totalPlaytimeText = "0h 00m 00s";
    private readonly DispatcherTimer _ticker;

    public HomeViewModel()
    {
        InstanceManager.Changed += Rebuild;
        Rebuild();
        // Playtime ticks live for running instances.
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => TickPlaytime();
        _ticker.Start();
        TickPlaytime();
    }

    private void TickPlaytime()
    {
        long total = SettingsService.Current.DeletedPlaytimeSeconds;
        foreach (var i in InstanceManager.Instances)
        {
            total += i.TotalPlaySeconds;
            if (i.SessionStart != null) i.TickPlaytime();
        }
        TotalPlaytimeText = Instance.FormatClock(total);
    }

    partial void OnFilterChanged(string value) => Rebuild();

    public void Rebuild()
    {
        foreach (var g in Groups) _expanded[g.Key ?? ""] = g.IsExpanded;
        Groups.Clear();

        var all = InstanceManager.Instances
            .Where(i => Filter.Length == 0 || i.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.Config.LastPlayed ?? i.Config.Created)
            .ToList();
        var cats = SettingsService.Current.Categories;

        foreach (var cat in cats)
        {
            var g = new CategoryGroup { Name = cat, Key = cat, IsExpanded = _expanded.GetValueOrDefault(cat, true) };
            foreach (var i in all.Where(i => i.Config.Category == cat)) g.Items.Add(i);
            if (Filter.Length == 0 || g.Items.Count > 0) Groups.Add(g);
        }

        var rest = all.Where(i => i.Config.Category == null || !cats.Contains(i.Config.Category)).ToList();
        if (rest.Count > 0 || (Groups.Count == 0 && InstanceManager.Instances.Count > 0))
        {
            var g = new CategoryGroup { Name = cats.Count == 0 ? L.T("All instances") : L.T("Uncategorized"), Key = null, IsExpanded = _expanded.GetValueOrDefault("", true) };
            foreach (var i in rest) g.Items.Add(i);
            Groups.Add(g);
        }
        IsEmpty = InstanceManager.Instances.Count == 0;
    }

    [RelayCommand]
    private void NewInstance() => Dialogs.Show(new NewInstanceViewModel());

    [RelayCommand]
    private void ImportFromLauncher() => Dialogs.Show(new ImportLauncherViewModel());

    [RelayCommand]
    private async Task NewCategory()
    {
        var name = await Dialogs.PromptAsync(L.T("New category"), L.T("Category name"), "", L.T("Create"));
        if (name != null) InstanceManager.AddCategory(name);
    }

    [RelayCommand]
    private async Task RenameCategory(CategoryGroup g)
    {
        if (g.Key == null) return;
        var name = await Dialogs.PromptAsync(L.T("Rename category"), L.T("Category name"), g.Name);
        if (name != null && name != g.Name && !SettingsService.Current.Categories.Contains(name))
            InstanceManager.RenameCategory(g.Key, name);
    }

    [RelayCommand]
    private async Task DeleteCategory(CategoryGroup g)
    {
        if (g.Key == null) return;
        if (await Dialogs.ConfirmAsync(L.T("Delete category"), L.F("Delete \"{0}\"? Its instances will move to Uncategorized; nothing is deleted from disk.", g.Name), L.T("Delete category")))
            InstanceManager.DeleteCategory(g.Key);
    }

    [RelayCommand]
    private void Open(Instance inst) => MainViewModel.Instance.OpenInstance(inst);

    [RelayCommand]
    private async Task Launch(Instance inst)
    {
        await LaunchService.PrimaryActionAsync(inst);
    }

    [RelayCommand]
    private async Task MoveToCategory(Instance inst)
    {
        const string none = "- No category -";
        var options = SettingsService.Current.Categories.ToList();
        options.Add(none);
        options.Add("+ New category…");
        var choice = await Dialogs.ChooseAsync(L.T("Move to category"), L.F("Choose a category for \"{0}\".", inst.Name), options);
        if (choice == null) return;
        if (choice == "+ New category…")
        {
            choice = await Dialogs.PromptAsync(L.T("New category"), L.T("Category name"), "", L.T("Create"));
            if (choice == null) return;
            InstanceManager.AddCategory(choice);
        }
        InstanceManager.SetCategory(inst, choice == none ? null : choice);
    }

    public void MoveInstance(Instance inst, string? category)
    {
        if (inst.Config.Category == category) return;
        InstanceManager.SetCategory(inst, category);
    }

    [RelayCommand]
    private void OpenFolder(Instance inst) => Dialogs.OpenPath(inst.GameDir);

    [RelayCommand]
    private async Task Delete(Instance inst)
    {
        if (inst.IsRunning || inst.IsBusy) { Dialogs.Toast(L.T("Stop the instance before deleting it."), true); return; }
        if (!await Dialogs.ConfirmAsync(L.T("Delete instance"),
                L.F("Permanently delete \"{0}\" including its worlds, mods and screenshots? This cannot be undone.", inst.Name), L.T("Delete"), true))
            return;
        try { InstanceManager.Delete(inst); }
        catch (Exception ex) { Dialogs.Toast(L.F("Delete failed: {0}", ex.Message), true); }
    }
}
