using System.Text;

namespace PyroLauncher.Desktop;

/// <summary>
/// The shared code and older views use Segoe Fluent Icons code points (Windows-only font). This maps them to the bundled
/// Lucide icon font so icons look the same on Windows, macOS and Linux.
/// </summary>
public static class Icons
{
    private static readonly Dictionary<char, char> Map = new()
    {
        [(char)0xE896] = (char)0xE0B2, // download
        [(char)0xE74D] = (char)0xE18E, // trash-2
        [(char)0xE8C8] = (char)0xE09E, // copy
        [(char)0xE8BB] = (char)0xE1B2, // x
        [(char)0xE8A7] = (char)0xE0B9, // external-link
        [(char)0xE838] = (char)0xE247, // folder-open
        [(char)0xE721] = (char)0xE151, // search
        [(char)0xE968] = (char)0xE153, // server
        [(char)0xE72C] = (char)0xE145, // refresh-cw
        [(char)0xE710] = (char)0xE13D, // plus
        [(char)0xE77B] = (char)0xE19F, // user
        [(char)0xE76C] = (char)0xE06F, // chevron-right
        [(char)0xE73E] = (char)0xE06C, // check
        [(char)0xE711] = (char)0xE1B2, // x
        [(char)0xE70D] = (char)0xE06D, // chevron-down
        [(char)0xE895] = (char)0xE145, // refresh-cw
        [(char)0xE72B] = (char)0xE048, // arrow-left
        [(char)0xEDE1] = (char)0xE155, // share
        [(char)0xEB9F] = (char)0xE0F6, // image
        [(char)0xEA86] = (char)0xE29C, // puzzle
        [(char)0xE922] = (char)0xE167, // square
        [(char)0xE921] = (char)0xE11C, // minus
        [(char)0xE90F] = (char)0xE1B1, // wrench
        [(char)0xE909] = (char)0xE09B, // compass
        [(char)0xE8FD] = (char)0xE106, // list
        [(char)0xE8EC] = (char)0xE17F, // tag
        [(char)0xE823] = (char)0xE087, // clock
        [(char)0xE7FC] = (char)0xE0DF, // gamepad-2
        [(char)0xE790] = (char)0xE1DD, // palette
        [(char)0xE774] = (char)0xE0E8, // globe
        [(char)0xE768] = (char)0xE13C, // play
        [(char)0xE756] = (char)0xE181, // terminal
        [(char)0xE713] = (char)0xE154, // settings
        [(char)0xE70F] = (char)0xE1F9, // pencil
        [(char)0xEBE8] = (char)0xE20C, // bug
        [(char)0xE9D9] = (char)0xE45F, // scroll-text
        [(char)0xE946] = (char)0xE0F9, // info
        [(char)0xE8FB] = (char)0xE06C, // check
        [(char)0xE8B7] = (char)0xE0D7, // folder
        [(char)0xE8B5] = (char)0xE22F, // import
        [(char)0xE8B3] = (char)0xE1D0, // list-checks
        [(char)0xE8AC] = (char)0xE265, // text-cursor-input
        [(char)0xE8A5] = (char)0xE0CC, // file-text
        [(char)0xE898] = (char)0xE19E, // upload
        [(char)0xE894] = (char)0xE148, // rotate-ccw
        [(char)0xE81C] = (char)0xE1F5, // history
        [(char)0xE80F] = (char)0xE0F5, // house
        [(char)0xE7BA] = (char)0xE193, // triangle-alert
        [(char)0xE7B8] = (char)0xE129, // package
        [(char)0xE787] = (char)0xE063, // calendar
        [(char)0xE783] = (char)0xE077, // circle-alert
        [(char)0xE777] = (char)0xE07B, // circle-arrow-up
        [(char)0xE76F] = (char)0xE3B1, // grip
        [(char)0xE76B] = (char)0xE06E, // chevron-left
        [(char)0xE71A] = (char)0xE167, // square
        [(char)0xE706] = (char)0xE178, // sun
        [(char)0xE923] = (char)0xE09E, // copy
    };

    /// <summary>Replaces Segoe glyphs in <paramref name="text"/> with their Lucide equivalents.</summary>
    public static string Convert(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) sb.Append(Map.TryGetValue(c, out var m) ? m : c);
        return sb.ToString();
    }
}
