using System.IO;
using System.Text.Json.Serialization;
using PyroLauncher.Core;

namespace PyroLauncher.Accounts;

/// <summary>A skin saved in the local library.</summary>
public sealed class SavedSkin
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Slim { get; set; }
    /// <summary>Mojang texture URL this skin had when it was last worn; used to mark the skin that is currently on the account.</summary>
    public string? TextureUrl { get; set; }
    public DateTime Added { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public string FilePath => Path.Combine(AppPaths.Skins, Id + ".png");
}

/// <summary>Named skins kept on disk so they can be swapped onto any Microsoft account.</summary>
public static class SkinLibrary
{
    private static string IndexFile => Path.Combine(AppPaths.Skins, "skins.json");
    private static readonly object Gate = new();

    public static List<SavedSkin> Load()
    {
        lock (Gate)
            return (JsonUtil.Load<List<SavedSkin>>(IndexFile) ?? []).Where(s => File.Exists(s.FilePath)).ToList();
    }

    private static void Save(IEnumerable<SavedSkin> skins)
    {
        lock (Gate) JsonUtil.Save(IndexFile, skins.ToList());
    }

    private static void Update(Action<List<SavedSkin>> change)
    {
        lock (Gate)
        {
            var all = JsonUtil.Load<List<SavedSkin>>(IndexFile) ?? [];
            change(all);
            Save(all);
        }
    }

    public static SavedSkin Add(string name, byte[] png, bool slim, string? textureUrl = null)
    {
        var skin = new SavedSkin { Name = name.Trim(), Slim = slim, TextureUrl = textureUrl };
        Directory.CreateDirectory(AppPaths.Skins);
        File.WriteAllBytes(skin.FilePath, png);
        Update(all => all.Add(skin));
        return skin;
    }

    /// <summary>Writes changed name / variant / texture URL back to the index.</summary>
    public static void Store(SavedSkin skin) => Update(all =>
    {
        var i = all.FindIndex(s => s.Id == skin.Id);
        if (i >= 0) all[i] = skin;
    });

    public static void Remove(SavedSkin skin)
    {
        Update(all => all.RemoveAll(s => s.Id == skin.Id));
        try { File.Delete(skin.FilePath); } catch (Exception ex) { Log.Warn($"Could not delete skin file: {ex.Message}"); }
    }
}

/// <summary>Flat front-view renders of skin textures for thumbnails.</summary>
public static class SkinRender
{
    /// <summary>A 16×32 front view (head, body, arms, legs and their overlay layers) as straight-alpha BGRA, or null if the image isn't a skin.</summary>
    public static (int Width, int Height, byte[] Bgra)? Front(byte[] png, bool slim)
    {
        if (Ui.Platform?.DecodePixels(png) is not { Width: 64, Height: 64 or 32 } src) return null;
        const int W = 16, H = 32;
        var dst = new byte[W * H * 4];
        var modern = src.Height == 64;
        var arm = slim ? 3 : 4;

        void Blit(int sx, int sy, int w, int h, int dx, int dy, bool overlay, bool mirror = false)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int s = ((sy + y) * src.Width + sx + (mirror ? w - 1 - x : x)) * 4;
                    int d = ((dy + y) * W + dx + x) * 4;
                    int a = src.Bgra[s + 3];
                    if (!overlay) a = 255;
                    else if (a == 0) continue;
                    for (int c = 0; c < 3; c++) dst[d + c] = (byte)((src.Bgra[s + c] * a + dst[d + c] * (255 - a)) / 255);
                    dst[d + 3] = 255;
                }
        }

        // Head + hat
        Blit(8, 8, 8, 8, 4, 0, false);
        Blit(40, 8, 8, 8, 4, 0, true);
        // Body
        Blit(20, 20, 8, 12, 4, 8, false);
        // Right arm (viewer's left) and right leg
        Blit(44, 20, arm, 12, 4 - arm, 8, false);
        Blit(4, 20, 4, 12, 4, 20, false);
        if (modern)
        {
            Blit(36, 52, arm, 12, 12, 8, false);   // left arm
            Blit(20, 52, 4, 12, 8, 20, false);     // left leg
            Blit(20, 36, 8, 12, 4, 8, true);       // jacket
            Blit(44, 36, arm, 12, 4 - arm, 8, true);
            Blit(52, 52, arm, 12, 12, 8, true);
            Blit(4, 36, 4, 12, 4, 20, true);
            Blit(4, 52, 4, 12, 8, 20, true);
        }
        else
        {
            // Legacy 64×32 skins reuse the right limbs, mirrored.
            Blit(44, 20, arm, 12, 12, 8, false, true);
            Blit(4, 20, 4, 12, 8, 20, false, true);
        }
        return (W, H, dst);
    }

    public static object? FrontImage(byte[] png, bool slim) =>
        Front(png, slim) is { } px ? Ui.Platform.ImageFromPixels(px.Width, px.Height, px.Bgra) : null;
}
