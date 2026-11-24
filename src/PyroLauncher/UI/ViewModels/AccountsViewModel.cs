using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Accounts;
using PyroLauncher.Core;

namespace PyroLauncher.UI.ViewModels;

public partial class AccountsViewModel : ObservableObject
{
    /// <summary>Accounts shown to the user: offline accounts are hidden unless developer mode is on.</summary>
    public System.ComponentModel.ICollectionView Accounts { get; } = new System.Windows.Data.ListCollectionView(AccountManager.Accounts)
    {
        Filter = o => o is Account a && AccountManager.IsUsable(a),
    };

    public bool DeveloperMode => AccountManager.OfflineAllowed;

    [ObservableProperty] private string _offlineName = "";
    [ObservableProperty] private string? _offlineError;
    [ObservableProperty] private DeviceCodeInfo? _deviceCode;
    [ObservableProperty] private bool _isSigningIn;
    [ObservableProperty] private string? _msStatus;
    [ObservableProperty] private string? _msError;
    private CancellationTokenSource? _cts;

    public bool HasAccounts => !Accounts.IsEmpty;

    public AccountsViewModel()
    {
        AccountManager.Accounts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAccounts));
        AccountManager.DeveloperModeChanged += () =>
        {
            Accounts.Refresh();
            OnPropertyChanged(nameof(HasAccounts));
            OnPropertyChanged(nameof(DeveloperMode));
        };
    }

    [RelayCommand]
    private void Select(Account acc) => AccountManager.Select(acc);

    [RelayCommand]
    private async Task Remove(Account acc)
    {
        if (await Dialogs.ConfirmAsync("Remove account", $"Remove {acc.Username} from the launcher?", "Remove", true))
            AccountManager.Remove(acc);
    }

    [RelayCommand]
    private void AddOffline()
    {
        var name = OfflineName.Trim();
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_]{3,16}$"))
        {
            OfflineError = "Use 3–16 characters: letters, digits and underscore.";
            return;
        }
        OfflineError = null;
        AccountManager.AddOffline(name);
        OfflineName = "";
        Dialogs.Toast($"Added offline account {name}");
    }

    [RelayCommand]
    private async Task SignInMicrosoft()
    {
        MsError = null;
        _cts = new CancellationTokenSource();
        IsSigningIn = true;
        MsStatus = "Requesting sign-in code…";
        try
        {
            DeviceCode = await MicrosoftAuth.StartDeviceCodeAsync(_cts.Token);
            MsStatus = "Waiting for you to finish signing in in your browser…";
            try { Clipboard.SetText(DeviceCode.UserCode); } catch { }
            Dialogs.OpenUrl(DeviceCode.VerificationUri);
            var acc = await MicrosoftAuth.PollAndLoginAsync(DeviceCode, _cts.Token);
            AccountManager.AddOrUpdate(acc);
            Dialogs.Toast($"Signed in as {acc.Username}");
        }
        catch (OperationCanceledException ex) when (_cts.IsCancellationRequested == false)
        {
            MsError = ex.Message;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Microsoft sign-in failed", ex);
            MsError = ex.Message;
        }
        finally
        {
            IsSigningIn = false;
            DeviceCode = null;
            MsStatus = null;
        }
    }

    [RelayCommand]
    private void CancelSignIn() => _cts?.Cancel();

    [RelayCommand]
    private void CopyCode()
    {
        if (DeviceCode == null) return;
        try { Clipboard.SetText(DeviceCode.UserCode); Dialogs.Toast("Code copied"); } catch { }
    }

    [RelayCommand]
    private void OpenVerification() => Dialogs.OpenUrl(DeviceCode?.VerificationUri);

    [RelayCommand]
    private void Close()
    {
        _cts?.Cancel();
        Dialogs.Close(this);
    }
}
