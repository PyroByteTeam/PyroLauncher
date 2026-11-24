using PyroLauncher.UI;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using PyroLauncher.Accounts;
using PyroLauncher.Core;
using PyroLauncher.Instances;

namespace PyroLauncher.Minecraft;

public static class GameLauncher
{
    public record Prepared(ResolvedVersion Version, PreparedGame Game, string Java, GameSettings Settings);

    /// <summary>Downloads and installs everything the instance needs (loader, libraries, assets, Java) without starting the game.</summary>
    public static async Task<Prepared> InstallAsync(Instance inst, IProgress<TaskProgress> progress, CancellationToken ct)
    {
        var cfg = inst.Config;
        Directory.CreateDirectory(inst.GameDir);
        void L(string s) => inst.AppendLog(s);

        var versionId = await LoaderService.EnsureInstalledAsync(inst, progress, L, ct);
        var version = VersionResolver.Resolve(versionId);

        var settings = cfg.Overrides.Over(SettingsService.Current.Game);
        string java;
        if (!string.IsNullOrWhiteSpace(settings.JavaPath) && File.Exists(settings.JavaPath))
            java = settings.JavaPath!;
        else
            java = await JavaManager.EnsureAsync(version.JavaMajor, progress, ct);
        L($"[PyroLauncher] Java: {java}");

        var game = await GameInstaller.PrepareAsync(version, inst.GameDir, progress, ct);
        inst.MarkInstalled();
        return new Prepared(version, game, java, settings);
    }

    /// <summary>Installs everything needed and starts the game. Returns the running process.</summary>
    public static async Task<Process> LaunchAsync(Instance inst, Account account, IProgress<TaskProgress> progress, CancellationToken ct)
    {
        void L(string s) => inst.AppendLog(s);
        L($"[PyroLauncher] Launching {inst.Config.Name} ({inst.VersionText}) as {account.Username}");

        progress.Report(new TaskProgress(UI.L.T("Checking account…")));
        await AccountManager.EnsureValidAsync(account, ct);

        var (version, game, java, settings) = await InstallAsync(inst, progress, ct);

        progress.Report(new TaskProgress(UI.L.T("Extracting natives…")));
        GameInstaller.ExtractNatives(game, inst.NativesDir);

        progress.Report(new TaskProgress(UI.L.T("Starting game…")));
        var args = BuildArguments(inst, version, game, account, settings);

        var psi = new ProcessStartInfo(java)
        {
            WorkingDirectory = inst.GameDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Windows command lines are limited to 32k chars; use a Java @argfile when needed (Java 9+).
        var totalLen = args.Sum(a => a.Length + 3);
        if (totalLen > 30000 && JavaManager.MapMajor(version.JavaMajor) > 8)
        {
            var argFile = Path.Combine(inst.Dir, "launch-args.txt");
            File.WriteAllLines(argFile, args.Select(QuoteForArgFile));
            psi.ArgumentList.Add("@" + argFile);
        }
        else
        {
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        L("[PyroLauncher] Command: " + java + " " + string.Join(" ", args.Select(a => a.Contains(account.AccessToken) && account.AccessToken.Length > 4 ? "<token>" : a)));

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) L(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) L(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return proc;
    }

    private static string QuoteForArgFile(string a)
    {
        var escaped = a.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    private static List<string> BuildArguments(Instance inst, ResolvedVersion v, PreparedGame game, Account account, GameSettings s)
    {
        var sep = Path.PathSeparator.ToString();
        var classpath = string.Join(sep, game.Classpath.Distinct(StringComparer.OrdinalIgnoreCase));
        var vars = new Dictionary<string, string>
        {
            ["auth_player_name"] = account.Username,
            ["version_name"] = v.Id,
            ["game_directory"] = inst.GameDir,
            ["assets_root"] = game.AssetsRoot,
            ["game_assets"] = game.GameAssetsDir,
            ["assets_index_name"] = game.AssetsIndexName,
            ["auth_uuid"] = account.Uuid,
            ["auth_access_token"] = account.AccessToken,
            ["auth_session"] = $"token:{account.AccessToken}:{account.Uuid}",
            ["auth_xuid"] = account.Xuid ?? "0",
            ["clientid"] = Accounts.MicrosoftAuth.ClientId,
            ["user_type"] = account.Type == AccountType.Microsoft ? "msa" : "legacy",
            ["user_properties"] = "{}",
            ["version_type"] = "PyroLauncher",
            ["natives_directory"] = inst.NativesDir,
            ["launcher_name"] = "PyroLauncher",
            ["launcher_version"] = Http.AppVersion,
            ["classpath"] = classpath,
            ["classpath_separator"] = sep,
            ["library_directory"] = AppPaths.Libraries,
            ["resolution_width"] = s.WindowWidth?.ToString() ?? "854",
            ["resolution_height"] = s.WindowHeight?.ToString() ?? "480",
        };
        var features = new Dictionary<string, bool> { ["has_custom_resolution"] = true };

        string Sub(string x)
        {
            var sb = new StringBuilder(x);
            foreach (var kv in vars) sb.Replace("${" + kv.Key + "}", kv.Value);
            return sb.ToString();
        }

        var args = new List<string>();

        // Memory + user JVM args
        // Java refuses to start when the initial heap is larger than the maximum, so cap it.
        var maxMb = s.MaxMemoryMb ?? 4096;
        var minMb = Math.Min(s.MinMemoryMb ?? 512, maxMb);
        args.Add($"-Xms{minMb}M");
        args.Add($"-Xmx{maxMb}M");
        args.AddRange(SplitArgs(s.JvmArgs ?? ""));

        if (v.JvmArgs.Count > 0)
        {
            foreach (var a in Expand(v.JvmArgs, features)) args.Add(Sub(a));
        }
        else
        {
            if (OperatingSystem.IsWindows()) args.Add("-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump");
            if (OperatingSystem.IsMacOS()) args.Add("-XstartOnFirstThread"); // LWJGL needs the main thread on macOS
            args.Add(Sub("-Djava.library.path=${natives_directory}"));
            args.Add("-Dminecraft.launcher.brand=PyroLauncher");
            args.Add("-cp");
            args.Add(classpath);
        }
        if (game.LoggingArgument != null) args.Add(game.LoggingArgument);

        args.Add(v.MainClass);

        if (v.GameArgs.Count > 0)
        {
            foreach (var a in Expand(v.GameArgs, features)) args.Add(Sub(a));
        }
        else if (v.LegacyArgs != null)
        {
            foreach (var a in v.LegacyArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries)) args.Add(Sub(a));
            args.Add("--width"); args.Add(vars["resolution_width"]);
            args.Add("--height"); args.Add(vars["resolution_height"]);
        }
        if (s.Fullscreen == true) args.Add("--fullscreen");

        // Drop feature args whose value placeholders were never substituted (quick play etc.)
        for (int i = args.Count - 1; i >= 0; i--)
        {
            if (!args[i].Contains("${")) continue;
            if (i > 0 && args[i - 1].StartsWith("--")) { args.RemoveAt(i); args.RemoveAt(i - 1); i--; }
            else args.RemoveAt(i);
        }
        return args;
    }

    private static IEnumerable<string> Expand(List<JsonNode> list, Dictionary<string, bool> features)
    {
        foreach (var node in list)
        {
            if (node is JsonValue jv && jv.TryGetValue<string>(out var str))
            {
                yield return str;
                continue;
            }
            if (node is JsonObject o)
            {
                if (!VersionResolver.RulesAllow(o.Get("rules"), features)) continue;
                var val = o.Get("value");
                if (val is JsonArray arr)
                {
                    foreach (var x in arr) if (x.AsStr() is { } s) yield return s;
                }
                else if (val.AsStr() is { } s) yield return s;
            }
        }
    }

    public static List<string> SplitArgs(string input)
    {
        var result = new List<string>();
        var cur = new StringBuilder();
        bool inQuotes = false;
        foreach (var c in input)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (cur.Length > 0) { result.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            cur.Append(c);
        }
        if (cur.Length > 0) result.Add(cur.ToString());
        return result;
    }
}
