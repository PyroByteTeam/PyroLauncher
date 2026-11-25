using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Core;

namespace PyroLauncher.UI.ViewModels;

public partial class LanguageChoice(string value, string label) : ObservableObject
{
    public string Value { get; } = value;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>One page of the welcome tour. Glyph is a Segoe Fluent code point (shown with the Icon converter).</summary>
public record TourSlide(string Glyph, string Title, string Text, string DemoPage);

/// <summary>"Which of these two do you prefer?" for one layout setting. DemoA / DemoB are the demo UI pages shown for each option.</summary>
public partial class LayoutQuestion(string demoA, string demoB, string title, string labelA, string textA, string labelB, string textB, bool currentIsA, Action<bool> apply)
    : ObservableObject
{
    public string DemoA { get; } = demoA;
    public string DemoB { get; } = demoB;
    public string Title { get; } = title;
    public string LabelA { get; } = labelA;
    public string TextA { get; } = textA;
    public string LabelB { get; } = labelB;
    public string TextB { get; } = textB;
    [ObservableProperty] private bool _isA = currentIsA;
    public bool IsB => !IsA;
    partial void OnIsAChanged(bool value) => OnPropertyChanged(nameof(IsB));
    public void Apply() => apply(IsA);
}

/// <summary>First-run welcome: language → short tour of the pages (skippable after 10 s) → layout preferences.</summary>
public partial class OnboardingViewModel : ObservableObject
{
    private const int SkipDelaySeconds = 10;
    private static LauncherSettings S => SettingsService.Current;
    private readonly DispatcherTimer _skipTimer;

    /// <summary>"language", "tour", "layout" or "done".</summary>
    [ObservableProperty] private string _step = "language";

    public OnboardingViewModel()
    {
        _skipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _skipTimer.Tick += (_, _) =>
        {
            if (SkipCountdown > 0) SkipCountdown--;
            if (SkipCountdown == 0) _skipTimer.Stop();
        };
    }

    public int StepNumber => Step switch { "language" => 1, "tour" => 2, _ => 3 };
    public bool ReachedTour => StepNumber >= 2;
    public bool ReachedLayout => StepNumber >= 3;

    partial void OnStepChanged(string value)
    {
        OnPropertyChanged(nameof(StepNumber));
        OnPropertyChanged(nameof(ReachedTour));
        OnPropertyChanged(nameof(ReachedLayout));
        if (value == "tour") StartTour();
        if (value == "layout") StartLayout();
    }

    // ---------- Language ----------

    public List<LanguageChoice> LanguageOptions { get; } =
    [
        new("auto", L.T("Automatic (system)")) { IsSelected = S.Language == "auto" },
        .. Loc.Languages.Select(l => new LanguageChoice(l.Code, l.Name) { IsSelected = S.Language == l.Code }),
    ];

    [RelayCommand]
    private void ChooseLanguage(string code)
    {
        if (code != S.Language)
        {
            S.Language = code;
            SettingsService.Save();
            Loc.Instance.SetLanguage(code);
            MainViewModel.Instance.OnLanguageChanged();
        }
        foreach (var o in LanguageOptions) o.IsSelected = o.Value == code;
    }

    [RelayCommand] private void ContinueFromLanguage() => Step = "tour";

    // ---------- Tour ----------

    [ObservableProperty] private List<TourSlide> _slides = [];
    [ObservableProperty] private int _slideIndex;
    [ObservableProperty] private int _skipCountdown = SkipDelaySeconds;

    public TourSlide? Slide => SlideIndex >= 0 && SlideIndex < Slides.Count ? Slides[SlideIndex] : null;
    public bool CanGoBack => SlideIndex > 0;
    public bool IsLastSlide => SlideIndex >= Slides.Count - 1;
    public string SlidePosition => $"{SlideIndex + 1} / {Slides.Count}";
    public bool CanSkip => SkipCountdown == 0;
    public string SkipText => CanSkip ? L.T("Skip tutorial") : L.F("Skip tutorial ({0})", SkipCountdown);

    partial void OnSlideIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Slide));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsLastSlide));
        OnPropertyChanged(nameof(SlidePosition));
    }

    partial void OnSlidesChanged(List<TourSlide> value) => OnSlideIndexChanged(SlideIndex);

    partial void OnSkipCountdownChanged(int value)
    {
        OnPropertyChanged(nameof(CanSkip));
        OnPropertyChanged(nameof(SkipText));
    }

    private void StartTour()
    {
        Slides =
        [
            new("", L.T("Home"), L.T("All your instances live here. Every instance is its own game folder with its own mods, settings and worlds. Create one with New instance, drag cards to sort them into categories and press play to start the game."), "home"),
            new("", L.T("Instances"), L.T("Open an instance to manage its mods, resource packs, shaders, screenshots, servers and logs. Search Modrinth and CurseForge from there, install with dependencies and update everything in one click."), "instance-tabs"),
            new("", L.T("Servers"), L.T("Server packs: download, configure and run a Minecraft server for your modpack, then manage its properties and players right from the launcher."), "servers-list"),
            new("", L.T("Discover"), L.T("Browse modpacks from Modrinth and CurseForge, read their pages and install one as a new instance."), "discover-list"),
            new("", L.T("Profile"), L.T("Sign in with your Microsoft account, then change your skin and cape. Use the button at the bottom of the sidebar to switch accounts."), "profile"),
            new("", L.T("Presets"), L.T("Save a set of mods as a preset and apply it to any instance, so your favourite mods are one click away."), "presets"),
            new("", L.T("Downloads"), L.T("Everything the launcher downloads shows up here. A progress pill also appears at the top of the window while something is downloading."), "downloads"),
            new("", L.T("Settings"), L.T("Java, memory, language and themes. You can make your own themes with colors, font size and a background image. Everything you choose next can be changed here later."), "settings"),
        ];
        SlideIndex = 0;
        SkipCountdown = SkipDelaySeconds;
        _skipTimer.Start();
    }

    [RelayCommand]
    private void NextSlide()
    {
        if (IsLastSlide) { _skipTimer.Stop(); Step = "layout"; }
        else SlideIndex++;
    }

    [RelayCommand] private void PreviousSlide() { if (CanGoBack) SlideIndex--; }

    [RelayCommand]
    private void SkipTour()
    {
        if (!CanSkip) return;
        _skipTimer.Stop();
        Step = "layout";
    }

    // ---------- Layout ----------

    [ObservableProperty] private List<LayoutQuestion> _questions = [];
    [ObservableProperty] private int _questionIndex;

    public LayoutQuestion? Question => QuestionIndex >= 0 && QuestionIndex < Questions.Count ? Questions[QuestionIndex] : null;
    public bool CanGoBackQuestion => QuestionIndex > 0;
    public string QuestionPosition => L.F("{0} of {1}", QuestionIndex + 1, Questions.Count);

    partial void OnQuestionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Question));
        OnPropertyChanged(nameof(CanGoBackQuestion));
        OnPropertyChanged(nameof(QuestionPosition));
    }

    partial void OnQuestionsChanged(List<LayoutQuestion> value) => OnQuestionIndexChanged(QuestionIndex);

    private void StartLayout()
    {
        Questions =
        [
            new("instance-tabs", "instance-grid", L.T("How should an instance page be organised?"),
                L.T("Top navigation"), L.T("Sections are tabs across the top of the instance page."),
                L.T("Grid"), L.T("An overview of section cards; drag them to rearrange."),
                S.InstanceMenuStyle == InstanceMenuStyle.TopNavigation,
                a => S.InstanceMenuStyle = a ? InstanceMenuStyle.TopNavigation : InstanceMenuStyle.Grid),
            new("content-list", "content-grid", L.T("How should mods and packs of an instance be listed?"),
                L.T("List"), L.T("Compact rows: more items on screen, easy to scan."),
                L.T("Grid"), L.T("Cards with bigger icons."),
                S.ContentListStyle == ContentListStyle.List,
                a => S.ContentListStyle = a ? ContentListStyle.List : ContentListStyle.Grid),
            new("discover-list", "discover-grid", L.T("How should Discover show search results?"),
                L.T("List"), L.T("Rows with the description next to the icon."),
                L.T("Grid"), L.T("Cards with a picture for every project."),
                S.DiscoverStyle == ContentListStyle.List,
                a => S.DiscoverStyle = a ? ContentListStyle.List : ContentListStyle.Grid),
            new("servers-list", "servers-cards", L.T("How should server packs be shown?"),
                L.T("List"), L.T("Compact rows."),
                L.T("Cards"), L.T("Cards with a picture and the server state."),
                S.ServerListStyle == ContentListStyle.List,
                a => S.ServerListStyle = a ? ContentListStyle.List : ContentListStyle.Grid),
        ];
        QuestionIndex = 0;
    }

    [RelayCommand]
    private void Pick(string option)
    {
        if (Question is not { } q) return;
        q.IsA = option == "A";
        q.Apply();
        SettingsService.NotifyAppearance();
        if (QuestionIndex < Questions.Count - 1) QuestionIndex++;
        else Step = "done";
    }

    [RelayCommand] private void PreviousQuestion() { if (CanGoBackQuestion) QuestionIndex--; }

    [RelayCommand] private void BackToLayout() { Step = "layout"; QuestionIndex = Questions.Count - 1; }

    // ---------- Done ----------

    [RelayCommand]
    private void Finish()
    {
        _skipTimer.Stop();
        S.OnboardingPending = false;
        SettingsService.Save();
        SettingsService.NotifyAppearance();
        MainViewModel.Instance.Onboarding = null;
    }
}
