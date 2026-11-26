using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Accounts;
using PyroLauncher.Core;
using PyroLauncher.Lan;

namespace PyroLauncher.UI.ViewModels;

/// <summary>Where the LAN page is in its flow. Off and Lobby follow the LAN state; the others are the user's steps.</summary>
public enum LanStep { Off, Disclaimer, Choose, Host, Join, Lobby }

/// <summary>
/// LAN page, one step at a time: turn LAN on → agree to the IP disclaimer → choose host or join → fill the form → lobby.
/// </summary>
public partial class LanViewModel : ObservableObject
{
    private const int FirstTimeCountdown = 30;
    private static LanSettings S => SettingsService.Current.Lan;
    public LanState Lan => LanService.State;

    public LanViewModel()
    {
        _lobbyName = S.LobbyName ?? "";
        _hostPassword = LoadPassword();
        _maxPlayersText = S.MaxPlayers.ToString();
        _portText = S.Port.ToString();
        _useUpnp = S.UseUpnp;
        _joinCode = S.LastJoinCode ?? "";
        Lan.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LanState.Mode))
            {
                TestText = null;
                WorldTestText = null;
                ManualWorldPortText = "";
                ShowDetails = false;
                StepChanged();
            }
            else if (e.PropertyName is nameof(LanState.Enabled)) StepChanged();
        };
        if (Lan.Enabled) BeginDisclaimer();
    }

    public bool LanEnabled
    {
        get => Lan.Enabled;
        set
        {
            if (value == Lan.Enabled) return;
            LanService.SetEnabled(value);
            if (value)
            {
                _chosen = LanStep.Choose;
                BeginDisclaimer();
            }
            OnPropertyChanged();
            StepChanged();
        }
    }

    // ---------- steps ----------
    private LanStep _chosen = LanStep.Choose;
    private bool _agreed;
    private CancellationTokenSource? _countdownCts;

    public LanStep Step =>
        !Lan.Enabled ? LanStep.Off
        : Lan.IsLobbyActive ? LanStep.Lobby
        : !_agreed ? LanStep.Disclaimer
        : _chosen;

    public bool IsOffStep => Step == LanStep.Off;
    public bool IsDisclaimerStep => Step == LanStep.Disclaimer;
    public bool IsChooseStep => Step == LanStep.Choose;
    public bool IsHostStep => Step == LanStep.Host;
    public bool IsJoinStep => Step == LanStep.Join;
    public bool IsLobbyStep => Step == LanStep.Lobby;
    /// <summary>The on/off switch sits in the page header once LAN is on (the Off step has its own big one).</summary>
    public bool ShowHeaderToggle => Lan.Enabled;

    private void StepChanged()
    {
        OnPropertyChanged(nameof(Step));
        OnPropertyChanged(nameof(IsOffStep));
        OnPropertyChanged(nameof(IsDisclaimerStep));
        OnPropertyChanged(nameof(IsChooseStep));
        OnPropertyChanged(nameof(IsHostStep));
        OnPropertyChanged(nameof(IsJoinStep));
        OnPropertyChanged(nameof(IsLobbyStep));
        OnPropertyChanged(nameof(ShowHeaderToggle));
        OnPropertyChanged(nameof(DefaultLobbyName));
    }

    /// <summary>Called whenever the page is opened: without a running lobby it starts again from the disclaimer.</summary>
    public void OnShown()
    {
        if (!Lan.Enabled || Lan.IsLobbyActive) return;
        _chosen = LanStep.Choose;
        BeginDisclaimer();
        StepChanged();
    }

    /// <summary>Seconds left before "I agree" unlocks (first time only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAgree), nameof(AgreeText), nameof(IsCountingDown))]
    private int _countdown;

    public bool CanAgree => Countdown == 0;
    public bool IsCountingDown => Countdown > 0;
    public string AgreeText => Countdown > 0 ? L.F("I agree ({0})", Countdown) : L.T("I agree");
    /// <summary>Agreed before: the disclaimer can be skipped straight away.</summary>
    public bool CanSkipDisclaimer => S.DisclaimerAccepted;

    private void BeginDisclaimer()
    {
        _agreed = false;
        _countdownCts?.Cancel();
        _countdownCts = null;
        Countdown = 0;
        OnPropertyChanged(nameof(CanSkipDisclaimer));
        if (!S.DisclaimerAccepted) _ = RunCountdown();
    }

    private async Task RunCountdown()
    {
        var cts = _countdownCts = new CancellationTokenSource();
        Countdown = FirstTimeCountdown;
        try
        {
            while (Countdown > 0)
            {
                await Task.Delay(1000, cts.Token);
                Countdown--;
            }
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private void Agree()
    {
        if (!CanAgree) return;
        if (!S.DisclaimerAccepted)
        {
            S.DisclaimerAccepted = true;
            SettingsService.Save();
        }
        _agreed = true;
        StepChanged();
    }

    /// <summary>Declining the disclaimer turns LAN off again.</summary>
    [RelayCommand]
    private void Decline()
    {
        _countdownCts?.Cancel();
        Countdown = 0;
        LanEnabled = false;
    }

    [RelayCommand]
    private void ChooseHost() { _chosen = LanStep.Host; StepChanged(); }

    [RelayCommand]
    private void ChooseJoin() { _chosen = LanStep.Join; StepChanged(); }

    [RelayCommand]
    private void Back() { _chosen = LanStep.Choose; StepChanged(); }

    /// <summary>Connection details, ports and tests are folded away in the lobby until asked for.</summary>
    [ObservableProperty] private bool _showDetails;

    [RelayCommand]
    private void ToggleDetails() => ShowDetails = !ShowDetails;

    // The host and join forms are remembered as they are edited, so the same lobby can be started again next time.
    partial void OnLobbyNameChanged(string value) { S.LobbyName = value.Trim(); SettingsService.Save(); }
    partial void OnHostPasswordChanged(string value) { SavePassword(value); SettingsService.Save(); }
    partial void OnUseUpnpChanged(bool value) { S.UseUpnp = value; SettingsService.Save(); }
    partial void OnJoinCodeChanged(string value) { S.LastJoinCode = value.Trim(); SettingsService.Save(); }

    partial void OnMaxPlayersTextChanged(string value)
    {
        if (!int.TryParse(value.Trim(), out var n) || n is < 2 or > 32) return;
        S.MaxPlayers = n;
        SettingsService.Save();
    }

    partial void OnPortTextChanged(string value)
    {
        if (!int.TryParse(value.Trim(), out var p) || p is < 1024 or > 65535) return;
        S.Port = p;
        SettingsService.Save();
    }

    private static string LoadPassword()
    {
        if (string.IsNullOrEmpty(S.ProtectedPassword)) return "";
        try { return System.Text.Encoding.UTF8.GetString(SecureStore.Unprotect(Convert.FromBase64String(S.ProtectedPassword))); }
        catch { return ""; }
    }

    private static void SavePassword(string password)
    {
        try
        {
            S.ProtectedPassword = password.Length == 0 ? null
                : Convert.ToBase64String(SecureStore.Protect(System.Text.Encoding.UTF8.GetBytes(password)));
        }
        catch (Exception ex) { Log.Warn("Could not save the lobby password: " + ex.Message); }
    }

    public string DefaultLobbyName => AccountManager.Selected is { } a ? L.F("{0}'s lobby", a.Username) : L.T("Lobby name");

    // ---------- host ----------
    [ObservableProperty] private string _lobbyName;
    [ObservableProperty] private string _hostPassword = "";
    [ObservableProperty] private string _maxPlayersText;
    [ObservableProperty] private string _portText;
    [ObservableProperty] private bool _useUpnp;
    [ObservableProperty] private string _manualWorldPortText = "";

    [RelayCommand]
    private void Host()
    {
        if (!int.TryParse(PortText.Trim(), out var port) || port is < 1024 or > 65535)
        {
            Dialogs.Toast(L.T("The lobby port must be a number from 1024 to 65535."), true);
            return;
        }
        var max = int.TryParse(MaxPlayersText.Trim(), out var m) ? m : 8;
        // Saved (with name, size, port and UPnP, which LanService.Host stores) so the same lobby can be hosted next time.
        SavePassword(HostPassword);
        try { LanService.Host(LobbyName, HostPassword, max, port, UseUpnp); }
        catch (LanException ex) { Dialogs.Toast(ex.Message, true); }
    }

    [RelayCommand]
    private async Task StopLobby()
    {
        if (Lan.IsHosting && Lan.Players.Count > 1 &&
            !await Dialogs.ConfirmAsync(L.T("Close lobby"), L.T("Everyone in the lobby will be disconnected."), L.T("Close lobby"), true)) return;
        LanService.Stop();
    }

    [RelayCommand]
    private void ApplyWorldPort()
    {
        var t = ManualWorldPortText.Trim();
        if (t.Length == 0) { LanService.SetManualWorldPort(null); return; }
        if (!int.TryParse(t, out var p) || p is < 1 or > 65535)
        {
            Dialogs.Toast(L.T("Enter the port number Minecraft showed in chat, e.g. 51234."), true);
            return;
        }
        LanService.SetManualWorldPort(p);
    }

    [RelayCommand]
    private async Task CopyInvite()
    {
        if (Lan.InviteCode is not { } code) return;
        try { await Ui.Platform.SetClipboardTextAsync(code); Dialogs.Toast(L.T("Invite code copied. Send it only to people you trust.")); } catch { }
    }

    [RelayCommand]
    private async Task Kick(LanPlayerItem? p)
    {
        if (p == null) return;
        if (await Dialogs.ConfirmAsync(L.F("Kick {0}?", p.Name), L.T("They can join again with the invite code."), L.T("Kick"), true))
            LanService.Kick(p);
    }

    [RelayCommand]
    private async Task Ban(LanPlayerItem? p)
    {
        if (p == null) return;
        if (await Dialogs.ConfirmAsync(L.F("Ban {0}?", p.Name),
                L.F("Everyone connecting from {0} will be refused until you remove the ban. People sharing that internet connection (same home or provider address) are blocked too.", p.Ip ?? "?"),
                L.T("Ban"), true))
            LanService.Ban(p);
    }

    [RelayCommand]
    private void Unban(LanBan? b)
    {
        if (b != null) LanService.Unban(b);
    }

    // ---------- join ----------
    [ObservableProperty] private string _joinCode;
    [ObservableProperty] private string _joinPassword = "";

    [RelayCommand]
    private async Task Join()
    {
        if (Lan.Busy) return;
        try
        {
            await LanService.JoinAsync(JoinCode, JoinPassword);
            JoinPassword = "";
        }
        catch (LanException ex) { Dialogs.Toast(ex.Message, true); }
        catch (Exception ex)
        {
            Log.Error("LAN join failed", ex);
            Dialogs.Toast(L.F("Could not join: {0}", ex.Message), true);
        }
    }

    [RelayCommand]
    private void Leave() => LanService.Stop();

    [RelayCommand]
    private async Task CopyLocalAddress()
    {
        if (Lan.LocalAddress.Length == 0) return;
        try { await Ui.Platform.SetClipboardTextAsync(Lan.LocalAddress); Dialogs.Toast(L.F("Copied {0}", Lan.LocalAddress)); } catch { }
    }

    // ---------- connection test ----------
    [ObservableProperty] private bool _testing;
    [ObservableProperty] private string? _testText;
    [ObservableProperty] private bool _testFailed;
    [ObservableProperty] private string? _worldTestText;

    /// <summary>Tests the lobby in the join box (before joining) or the current lobby (while joined).</summary>
    [RelayCommand]
    private async Task TestConnection()
    {
        if (Testing) return;
        Testing = true;
        TestFailed = false;
        TestText = L.T("Testing…");
        try
        {
            var progress = new Progress<int>(n => TestText = L.F("Testing… {0}/10", n));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var r = Lan.IsJoined
                ? await LanService.TestCurrentAsync(progress, cts.Token)
                : await LanService.TestAsync(JoinCode, progress, cts.Token);
            TestText = Describe(r);
            TestFailed = r.Pings.Count == 0;
        }
        catch (LanException ex) { TestText = ex.Message; TestFailed = true; }
        catch (Exception ex) { TestText = L.F("Test failed: {0}", ex.Message); TestFailed = true; }
        finally { Testing = false; }
    }

    private static string Describe(LanTestResult r)
    {
        var lines = new List<string>
        {
            L.F("Lobby \"{0}\" reached at {1} (TCP port {2}).", r.LobbyName, LanProtocol.FormatEndpoint(r.Address, r.Port), r.Port),
            r.Pings.Count > 0
                ? L.F("Ping: {0:0} ms average, {1:0}-{2:0} ms, jitter {3:0.0} ms, lost {4}%.", r.Avg, r.Min, r.Max, r.Jitter, r.LossPercent)
                : L.T("Ping: no answers to the test packets."),
            L.F("Connection set up in {0:0} ms.", r.ConnectMs),
            L.F("Players: {0}/{1}{2}", r.Players, r.MaxPlayers, r.PasswordRequired ? " · " + L.T("password required") : ""),
            r.World != null ? L.F("Open world: {0}", r.World) : L.T("The host has not opened a world to LAN yet."),
            Verdict(r),
        };
        return string.Join("\n", lines);
    }

    private static string Verdict(LanTestResult r)
    {
        if (r.Pings.Count == 0 || r.LossPercent >= 30) return L.T("Verdict: unstable - expect disconnects.");
        if (r.Avg < 60 && r.LossPercent == 0) return L.T("Verdict: excellent for playing together.");
        if (r.Avg < 150 && r.LossPercent < 10) return L.T("Verdict: good, playable.");
        return L.T("Verdict: playable, but you will notice lag.");
    }

    /// <summary>Asks the host's world for its status through the tunnel, the same way Minecraft's server list does.</summary>
    [RelayCommand]
    private async Task TestWorld()
    {
        if (Testing) return;
        Testing = true;
        WorldTestText = L.T("Asking the host's world…");
        try
        {
            var (status, ping) = await LanService.TestWorldAsync(CancellationToken.None);
            var version = status.Obj("version").Str("name") ?? "?";
            var online = status.Obj("players").Int("online");
            var max = status.Obj("players").Int("max");
            WorldTestText = L.F("The world answers through the tunnel: Minecraft {0}, {1}/{2} players, {3:0} ms.", version, online, max, ping.TotalMilliseconds);
        }
        catch (LanException ex) { WorldTestText = ex.Message; }
        catch (Exception ex) { WorldTestText = L.F("Test failed: {0}", ex.Message); }
        finally { Testing = false; }
    }
}
