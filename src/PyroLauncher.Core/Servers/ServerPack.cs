using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using PyroLauncher.Core;
using PyroLauncher.Instances;
using PyroLauncher.UI;

namespace PyroLauncher.Servers;

public class ServerPackConfig
{
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? LastStarted { get; set; }
    /// <summary>Maximum heap in MB; 0 until the first-run setup has been completed.</summary>
    public int MemoryMb { get; set; }
    public bool EulaAccepted { get; set; }
    public string? BaseInstanceId { get; set; }
    /// <summary>Instances that show a start/stop button for this server.</summary>
    public List<string> LinkedInstances { get; set; } = [];
    /// <summary>Arguments after the memory flags, e.g. ["-jar", "server.jar", "nogui"]. Set by the installer.</summary>
    public List<string> LaunchArgs { get; set; } = [];
    public int JavaMajor { get; set; } = 21;
    public string? InstalledKey { get; set; }
    public string? JvmArgs { get; set; }
    /// <summary>Java executable to start the server with; null uses the managed Java for this Minecraft version.</summary>
    public string? JavaPath { get; set; }
}

public partial class ServerPack : ObservableObject
{
    public string Id { get; }
    public string Dir { get; }
    public string ServerDir => Path.Combine(Dir, "server");
    public string ConfigPath => Path.Combine(Dir, "server.json");
    public string PropertiesPath => Path.Combine(ServerDir, "server.properties");
    public ServerPackConfig Config { get; }
    /// <summary>Instance view of the server folder: lets the content pages (mods, updates) work on it unchanged.</summary>
    public Instance Instance { get; }

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private double _progress = -1;
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private long _memoryBytes;
    [ObservableProperty] private DateTime? _startedAt;

    public ObservableCollection<string> OnlinePlayers { get; } = [];
    public ObservableCollection<LogLine> Console => Instance.LogLines;
    public event Action? PlayersChanged;

    private Process? _process;
    private DispatcherTimer? _statsTimer;
    private TimeSpan _lastCpu;
    private DateTime _lastSample;
    private bool _killed;

    public ServerPack(string dir, ServerPackConfig config, InstanceConfig instanceConfig)
    {
        Dir = dir;
        Id = Path.GetFileName(dir);
        Config = config;
        Instance = new Instance(dir, instanceConfig, "server");
    }

    public string Name => Instance.Name;
    public string Initial => Instance.Initial;
    public object? Icon => Instance.Icon;
    public string VersionText => Instance.VersionText;
    public string CurrentKey => Instance.CurrentKey;
    public bool IsInstalled => Config.InstalledKey == CurrentKey && Config.LaunchArgs.Count > 0;
    public bool NeedsSetup => Config.MemoryMb <= 0 || !Config.EulaAccepted;

    public int MaxPlayers => int.TryParse(ServerProperties.Load(PropertiesPath).Get("max-players"), out var n) ? n : 20;
    public string PlayerCountText => $"{OnlinePlayers.Count} / {MaxPlayers}";
    public string MemoryText => Config.MemoryMb > 0 ? $"{Config.MemoryMb / 1024.0:0.#} GB" : "-";
    /// <summary>Short state for badges; the full progress message is in <see cref="Status"/>.</summary>
    public string StateText => IsBusy ? L.T("Working…") : IsRunning ? (Status ?? (IsReady ? L.T("Online") : L.T("Starting…"))) : L.T("Offline");
    public string UptimeText => StartedAt is { } s ? Instances.Instance.FormatClock((long)(DateTime.Now - s).TotalSeconds) : "-";
    public string MemoryUsageText => IsRunning ? $"{MemoryBytes / 1024.0 / 1024 / 1024:0.00} GB" : "-";
    public double MemoryUsageFraction => Config.MemoryMb > 0 ? Math.Min(1, MemoryBytes / (Config.MemoryMb * 1024.0 * 1024)) : 0;
    public string CpuText => IsRunning ? $"{CpuPercent:0}%" : "-";
    public string ToggleGlyph => IsBusy ? "" : IsRunning ? "" : IsInstalled ? "" : "";
    public string ToggleTip => IsBusy ? Status ?? "" : IsRunning ? L.T("Stop server") : IsInstalled ? L.T("Start server") : L.T("Install server");

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(IsRunning) or nameof(IsBusy) or nameof(IsReady) or nameof(Status):
                base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(StateText)));
                base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ToggleGlyph)));
                base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ToggleTip)));
                base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(CpuText)));
                base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(MemoryUsageText)));
                break;
            case nameof(CpuPercent): base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(CpuText))); break;
            case nameof(MemoryBytes):
                base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(MemoryUsageText)));
                base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(MemoryUsageFraction)));
                base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(UptimeText)));
                break;
        }
    }

    public void OnPropertyChangedPublic(string name) => OnPropertyChanged(name);

    public void Save()
    {
        try { JsonUtil.Save(ConfigPath, Config); Instance.Save(); }
        catch (Exception ex) { Log.Error($"Saving server pack {Id} failed", ex); }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Initial));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(MemoryText));
        OnPropertyChanged(nameof(PlayerCountText));
        Instance.Refresh();
    }

    // ------------------------------------------------------------------ process

    public void Start(string java)
    {
        if (IsRunning) return;
        var psi = new ProcessStartInfo(java)
        {
            WorkingDirectory = ServerDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add($"-Xmx{Config.MemoryMb}M");
        psi.ArgumentList.Add($"-Xms{Math.Min(Config.MemoryMb, 1024)}M");
        foreach (var a in SplitArgs(Config.JvmArgs)) psi.ArgumentList.Add(a);
        foreach (var a in Config.LaunchArgs) psi.ArgumentList.Add(a);

        Instance.ClearLog();
        OnlinePlayers.Clear();
        _killed = false;
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) OnOutput(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) OnOutput(e.Data); };
        proc.Exited += (_, _) => Ui.Post(OnExited);
        Instance.AppendLog($"[PyroLauncher] Starting {Name}: java {string.Join(' ', psi.ArgumentList)}");
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        _process = proc;
        IsRunning = true;
        IsReady = false;
        StartedAt = DateTime.Now;
        Instance.IsRunning = true;
        Config.LastStarted = DateTime.Now;
        Save();

        _lastCpu = TimeSpan.Zero;
        _lastSample = DateTime.Now;
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statsTimer.Tick += (_, _) => SampleStats();
        _statsTimer.Start();
        PlayersChanged?.Invoke();
    }

    /// <summary>Splits a command line on spaces, keeping "quoted parts" together.</summary>
    public static List<string> SplitArgs(string? line)
    {
        var result = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (var ch in line ?? "")
        {
            if (ch == '"') { quoted = !quoted; any = true; }
            else if (char.IsWhiteSpace(ch) && !quoted) { if (any) result.Add(cur.ToString()); cur.Clear(); any = false; }
            else { cur.Append(ch); any = true; }
        }
        if (any) result.Add(cur.ToString());
        return result;
    }

    public static string JoinArgs(IEnumerable<string> args) => string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

    public void SendCommand(string command)
    {
        if (_process is not { HasExited: false } p) return;
        Instance.AppendLog("> " + command);
        try { p.StandardInput.WriteLine(command); p.StandardInput.Flush(); }
        catch (Exception ex) { Instance.AppendLog("[PyroLauncher] Couldn't send the command: " + ex.Message); }
    }

    /// <summary>Asks the server to save and shut down; kills it if it hasn't stopped after a while.</summary>
    public async Task StopAsync()
    {
        if (_process is not { } p || p.HasExited) return;
        Status = L.T("Stopping…");
        SendCommand("stop");
        try { await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token); }
        catch (OperationCanceledException) { Kill(); }
    }

    public bool IsProcessAlive => _process is { } p && !SafeExited(p);
    private static bool SafeExited(Process p) { try { return p.HasExited; } catch { return true; } }

    public void Kill()
    {
        if (_process is not { } p) return;
        _killed = true;
        try { if (!p.HasExited) p.Kill(true); } catch { }
    }

    private void OnExited()
    {
        int code = 0;
        try { code = _process?.ExitCode ?? 0; } catch { }
        _statsTimer?.Stop();
        _statsTimer = null;
        _process = null;
        IsRunning = false;
        IsReady = false;
        Instance.IsRunning = false;
        Status = null;
        StartedAt = null;
        CpuPercent = 0;
        MemoryBytes = 0;
        OnlinePlayers.Clear();
        OnPropertyChanged(nameof(PlayerCountText));
        PlayersChanged?.Invoke();
        Instance.AppendLog($"[PyroLauncher] Server stopped (exit code {code})");
        if (code != 0 && !_killed) Dialogs.Toast(L.F("{0} stopped with exit code {1}. See the console for details.", Name, code), true);
    }

    private void SampleStats()
    {
        if (_process is not { HasExited: false } p) return;
        try
        {
            p.Refresh();
            var cpu = p.TotalProcessorTime;
            var now = DateTime.Now;
            if (_lastCpu != TimeSpan.Zero)
                CpuPercent = Math.Clamp((cpu - _lastCpu).TotalMilliseconds / (now - _lastSample).TotalMilliseconds / Environment.ProcessorCount * 100, 0, 100);
            _lastCpu = cpu;
            _lastSample = now;
            MemoryBytes = p.WorkingSet64;
        }
        catch { }
    }

    // ------------------------------------------------------------------ output parsing

    [GeneratedRegex(@"\]: (?:\[[^\]]*\] )?([A-Za-z0-9_]{1,16}) joined the game")] private static partial Regex JoinRx();
    [GeneratedRegex(@"\]: (?:\[[^\]]*\] )?([A-Za-z0-9_]{1,16}) left the game")] private static partial Regex LeaveRx();
    [GeneratedRegex(@"There are \d+ (?:of a max of|/) ?\d+ players online:\s*(.*)$")] private static partial Regex ListRx();
    [GeneratedRegex(@"\]: Done \(")] private static partial Regex DoneRx();

    private void OnOutput(string line)
    {
        Instance.AppendLog(line);
        if (DoneRx().IsMatch(line)) Ui.Post(() => { IsReady = true; Status = null; });
        else if (JoinRx().Match(line) is { Success: true } j) Ui.Post(() => { if (!OnlinePlayers.Contains(j.Groups[1].Value)) OnlinePlayers.Add(j.Groups[1].Value); PlayersUpdated(); });
        else if (LeaveRx().Match(line) is { Success: true } l) Ui.Post(() => { OnlinePlayers.Remove(l.Groups[1].Value); PlayersUpdated(); });
        else if (ListRx().Match(line) is { Success: true } m)
        {
            var names = m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Ui.Post(() =>
            {
                OnlinePlayers.Clear();
                foreach (var n in names) OnlinePlayers.Add(n);
                PlayersUpdated();
            });
        }
    }

    private void PlayersUpdated()
    {
        OnPropertyChanged(nameof(PlayerCountText));
        PlayersChanged?.Invoke();
    }
}
