using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PyroLauncher.Core;

public enum JobState { Running, Paused, Completed, Failed, Cancelled }

/// <summary>A user-visible download operation (e.g. "Preparing My Pack", "Java 21"). Counters are thread-safe; UI props refresh on a timer.</summary>
public partial class DownloadJob : ObservableObject, IDisposable
{
    public string Title { get; }
    public DateTime Started { get; } = DateTime.Now;
    public CancellationTokenSource? Cts { get; }
    public bool CanCancel => Cts != null && IsActive;

    internal long Bytes;
    internal int FilesDone, FilesTotal;
    internal long CurFileBytes, CurFileTotal;
    internal volatile string? CurFile;
    private TaskCompletionSource? _resume;
    private long _lastBytes;
    private DateTime _lastTick = DateTime.Now;
    internal bool Registered;
    private bool _failed;

    [ObservableProperty] private JobState _state = JobState.Running;
    [ObservableProperty] private double _progress = -1;
    [ObservableProperty] private string _filesText = "";
    [ObservableProperty] private string _bytesText = "";
    [ObservableProperty] private string _speedText = "";
    [ObservableProperty] private string? _currentFile;
    public double SpeedBytes { get; private set; }

    public bool IsActive => State is JobState.Running or JobState.Paused;
    public bool IsPaused => State == JobState.Paused;
    public string StateText => State switch
    {
        JobState.Running => "Downloading",
        JobState.Paused => "Paused",
        JobState.Completed => "Done",
        JobState.Failed => "Failed",
        _ => "Cancelled",
    };

    internal DownloadJob(string title, CancellationTokenSource? cts)
    {
        Title = title;
        Cts = cts;
    }

    partial void OnStateChanged(JobState value)
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(CanCancel));
    }

    public void AddFiles(int n)
    {
        if (n <= 0) return;
        Interlocked.Add(ref FilesTotal, n);
        DownloadManager.Register(this);
    }

    public void FileDone() => Interlocked.Increment(ref FilesDone);

    public void AddBytes(long n)
    {
        Interlocked.Add(ref Bytes, n);
        Interlocked.Add(ref CurFileBytes, n);
    }

    public void BeginFile(string name, long total)
    {
        CurFile = name;
        Interlocked.Exchange(ref CurFileBytes, 0);
        Interlocked.Exchange(ref CurFileTotal, total);
    }

    public Task WaitIfPausedAsync(CancellationToken ct) => _resume is { } t ? t.Task.WaitAsync(ct) : Task.CompletedTask;

    public void Pause()
    {
        if (State != JobState.Running) return;
        Interlocked.CompareExchange(ref _resume, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), null);
        State = JobState.Paused;
    }

    public void Resume()
    {
        if (State != JobState.Paused) return;
        Interlocked.Exchange(ref _resume, null)?.TrySetResult();
        State = JobState.Running;
    }

    public void Cancel()
    {
        Resume();
        Cts?.Cancel();
    }

    public void Fail() => _failed = true;

    public void Dispose()
    {
        Interlocked.Exchange(ref _resume, null)?.TrySetResult();
        var final = Cts?.IsCancellationRequested == true ? JobState.Cancelled : _failed ? JobState.Failed : JobState.Completed;
        DownloadManager.RunOnUi(() =>
        {
            Tick();
            State = final;
            if (final == JobState.Completed) Progress = 1;
            SpeedText = "";
            CurrentFile = null;
        });
    }

    internal void Tick()
    {
        var now = DateTime.Now;
        var bytes = Interlocked.Read(ref Bytes);
        var dt = (now - _lastTick).TotalSeconds;
        if (dt > 0.2)
        {
            var inst = (bytes - _lastBytes) / dt;
            SpeedBytes = SpeedBytes <= 0 ? inst : SpeedBytes * 0.6 + inst * 0.4;
            _lastBytes = bytes;
            _lastTick = now;
        }
        if (State == JobState.Paused) SpeedBytes = 0;
        var done = Volatile.Read(ref FilesDone);
        var total = Volatile.Read(ref FilesTotal);
        var cur = Interlocked.Read(ref CurFileBytes);
        var curTotal = Interlocked.Read(ref CurFileTotal);
        Progress = total > 1 ? (double)done / total
            : curTotal > 0 ? Math.Min(1, (double)cur / curTotal)
            : total == 1 && done == 1 ? 1 : -1;
        FilesText = total == 1 ? "1 file" : $"{done:N0} / {total:N0} files";
        BytesText = DownloadManager.FormatBytes(bytes);
        SpeedText = State == JobState.Running && SpeedBytes > 1 ? DownloadManager.FormatBytes((long)SpeedBytes) + "/s" : "";
        CurrentFile = CurFile;
    }
}

public partial class HostStat : ObservableObject
{
    public string Host { get; init; } = "";
    [ObservableProperty] private int _active;
    [ObservableProperty] private int _requests;
    [ObservableProperty] private string _bytesText = "";
}

public partial class DownloadSummary : ObservableObject
{
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private double _progress = -1;
    [ObservableProperty] private string _text = "";
}

/// <summary>Tracks all downloads for the Downloads page. Operations open a job with <see cref="Begin"/>; nested code picks it up via AsyncLocal.</summary>
public static class DownloadManager
{
    private sealed class HostCounter
    {
        public int Active;
        public int Requests;
        public long Bytes;
    }

    private static readonly AsyncLocal<DownloadJob?> CurrentJob = new();
    private static readonly ConcurrentDictionary<string, HostCounter> HostCounters = new();
    private static DispatcherTimer? _timer;

    public static ObservableCollection<DownloadJob> Jobs { get; } = [];
    public static ObservableCollection<HostStat> Hosts { get; } = [];
    public static DownloadSummary Summary { get; } = new();

    public static DownloadJob? Current => CurrentJob.Value;

    public static void Init()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>Starts a job for the current async flow. Dispose it when the operation ends.</summary>
    public static DownloadJob Begin(string title, CancellationTokenSource? cts = null)
    {
        var job = new DownloadJob(title, cts);
        CurrentJob.Value = job;
        return job;
    }

    internal static void Register(DownloadJob job)
    {
        if (job.Registered) return;
        job.Registered = true;
        RunOnUi(() =>
        {
            Jobs.Insert(0, job);
            while (Jobs.Count > 30)
            {
                var old = Jobs.LastOrDefault(j => !j.IsActive);
                if (old == null) break;
                Jobs.Remove(old);
            }
        });
    }

    public static void HostStart(string host)
    {
        var c = HostCounters.GetOrAdd(host, _ => new HostCounter());
        Interlocked.Increment(ref c.Active);
    }

    /// <summary>Counts any request the launcher makes (API calls, images, downloads) so every contacted domain is listed.</summary>
    public static void HostSeen(string host)
    {
        var c = HostCounters.GetOrAdd(host, _ => new HostCounter());
        Interlocked.Increment(ref c.Requests);
    }

    public static void HostEnd(string host)
    {
        if (HostCounters.TryGetValue(host, out var c)) Interlocked.Decrement(ref c.Active);
    }

    public static void HostBytes(string host, long n)
    {
        if (HostCounters.TryGetValue(host, out var c)) Interlocked.Add(ref c.Bytes, n);
    }

    public static void PauseAll() { foreach (var j in Jobs) j.Pause(); }
    public static void ResumeAll() { foreach (var j in Jobs) j.Resume(); }

    public static void ClearFinished()
    {
        foreach (var j in Jobs.Where(j => !j.IsActive).ToList()) Jobs.Remove(j);
    }

    private static void Tick()
    {
        foreach (var j in Jobs) if (j.IsActive) j.Tick();

        foreach (var (host, c) in HostCounters)
        {
            var stat = Hosts.FirstOrDefault(h => h.Host == host);
            if (stat == null)
            {
                stat = new HostStat { Host = host };
                Hosts.Add(stat);
            }
            stat.Active = Math.Max(0, Volatile.Read(ref c.Active));
            stat.Requests = Volatile.Read(ref c.Requests);
            stat.BytesText = FormatBytes(Interlocked.Read(ref c.Bytes));
        }

        var active = Jobs.Where(j => j.IsActive).ToList();
        Summary.IsActive = active.Count > 0;
        Summary.ActiveCount = active.Count;
        if (active.Count > 0)
        {
            var withProgress = active.Where(j => j.Progress >= 0).ToList();
            Summary.Progress = withProgress.Count == 0 ? -1 : withProgress.Average(j => j.Progress);
            var speed = active.Sum(j => j.SpeedBytes);
            var paused = active.All(j => j.IsPaused);
            Summary.Text = paused ? "Paused"
                : $"{(Summary.Progress >= 0 ? $"{Summary.Progress * 100:0}%" : "…")}{(speed > 1 ? " · " + FormatBytes((long)speed) + "/s" : "")}";
        }
    }

    public static void RunOnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }

    public static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{b / 1024.0:0} KB",
        _ => $"{b} B",
    };
}
