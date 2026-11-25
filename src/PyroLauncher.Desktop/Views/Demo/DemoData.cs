using Avalonia.Media;
using PyroLauncher.UI;

namespace PyroLauncher.Desktop.Views.Demo;

/// <summary>Made-up content for the onboarding demo UI (never touches real instances or accounts).</summary>
public record DemoItem(string Name, string Sub, string Extra, string Color, bool On = true, bool Highlight = false)
{
    public string Initial => Name[..1];
    public IBrush Tint { get; } = new SolidColorBrush(Avalonia.Media.Color.Parse(Color));
}

public record DemoSection(string Name, string Glyph, string Count);

public static class DemoData
{
    public static readonly DemoItem[] Instances =
    [
        new("Vanilla+", "1.21.4 · Fabric", "12h 40m played", "#FF3E7C4F", Highlight: true),
        new("Create: Steam 'n' Rails", "1.20.1 · Forge", "31h 05m played", "#FF8A5A2E"),
        new("Skyblock with friends", "1.21.1 · NeoForge", "4h 12m played", "#FF3A5F8F"),
        new("Pure survival", "1.21.4 · Vanilla", "Never played", "#FF6B4A8A"),
    ];

    public static readonly DemoItem[] Mods =
    [
        new("Sodium", "by CaffeineMC", "0.6.13", "#FF4F9A6B"),
        new("Lithium", "by CaffeineMC", "0.15.1", "#FF4F7FA8"),
        new("Iris Shaders", "by coderbot", "1.8.8", "#FF7A5AB0", Highlight: true),
        new("Mod Menu", "by Prospector", "13.0.3", "#FF9A7B3E"),
        new("Xaero's Minimap", "by xaero96", "25.2.0", "#FF3E8A8A", false),
        new("Fabric API", "by modmuss50", "0.119.2", "#FF8A6F5A"),
    ];

    public static readonly DemoItem[] Projects =
    [
        new("Fabulously Optimized", "Improve your graphics and performance with this simple modpack.", "8.4M", "#FF3E7CA8"),
        new("Cobblemon Official", "Explore the world, catch and battle creatures with friends.", "3.1M", "#FFB0483E"),
        new("Adrenaserver", "Lightweight, fast server-side optimisation pack.", "1.9M", "#FF5A8A3E"),
        new("Better Adventures", "Exploration focused pack with new dungeons and biomes.", "1.2M", "#FF8A5AA8"),
        new("Create Astral", "Space themed tech progression built around Create.", "960k", "#FF2E6F8A"),
        new("Prominence II", "Big RPG pack with quests, bosses and magic.", "740k", "#FF9A7B3E"),
    ];

    public static readonly DemoItem[] Servers =
    [
        new("Survival SMP", "1.21.4 · Fabric", "Running · 3/20 players", "#FF3E7C4F", true),
        new("Create server", "1.20.1 · Forge", "Stopped", "#FF8A5A2E", false),
        new("Minigames", "1.21.1 · Paper", "Stopped", "#FF3A5F8F", false),
    ];

    public static readonly DemoItem[] Presets =
    [
        new("Performance", "Sodium, Lithium, FerriteCore, ModernFix", "9 mods", "#FF4F9A6B"),
        new("Quality of life", "Mod Menu, JEI, Jade, AppleSkin", "14 mods", "#FF9A7B3E"),
        new("Shaders", "Iris, Complementary Reimagined", "2 items", "#FF7A5AB0"),
    ];

    public static readonly DemoItem[] Downloads =
    [
        new("Create: Steam 'n' Rails", "Downloading mods · 84 / 212", "40", "#FF8A5A2E"),
        new("Java 21 (Adoptium)", "Extracting", "85", "#FF3A5F8F"),
        new("Iris Shaders 1.8.8", "Done", "100", "#FF7A5AB0"),
    ];

    /// <summary>Section cards of the grid instance menu: name, icon (Segoe code point) and item count.</summary>
    public static DemoSection[] Sections =>
    [
        new(L.T("Mods"), "", "48"), new(L.T("Resource Packs"), "", "3"), new(L.T("Shaders"), "", "2"),
        new(L.T("Screenshots"), "", "126"), new(L.T("Servers"), "", "4"), new(L.T("Logs"), "", "9"),
    ];
}
