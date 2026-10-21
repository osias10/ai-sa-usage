using System.Windows;
using System.Windows.Threading;
using System.Text.Json;
using AIUsage.Core;

namespace AIUsage.App;

public sealed partial class AppController
{
    private ResetMessageEngine _resetMessages = null!;
    private readonly DispatcherTimer _resetMessageTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    private Task _resetMessageWork = Task.CompletedTask;
    private readonly HashSet<string> _enabledResetAccounts = [];
    private readonly Dictionary<string, int> _resetSettingsRevisions = [];
    private readonly Dictionary<string, CancellationTokenSource> _resetMessageCancellations = [];
    private readonly Dictionary<string, DateTimeOffset> _resetMessageRetryAt = [];
    private readonly Dictionary<string, (string Label, string Detail)> _resetMessageNotices = [];
    private bool _resetStateSaveFailed;
    private readonly SemaphoreSlim _resetStateSaveGate = new(1, 1);
    private string? _savedResetState;
    // Isolated smoke verification never writes a user's state or calls a real provider.
    private Func<ResetMessageState, Task>? _saveResetStateForVerification;

    private void InitializeResetMessages(ResetMessageState? state)
    {
        _resetMessages = new(state);
        foreach (var account in Settings.Accounts)
        {
            if (SupportsResetMessage(account) && account.ResetMessageEnabled) _enabledResetAccounts.Add(account.Id);
            _resetMessages.SynchronizeSettings(account, ResetMessageEnabled(account), account.ResetMessageWindowId);
        }
        _resetMessageTimer.Tick += (_, _) =>
        {
            if (_resetMessageWork.IsCompleted && !_disposed && !_preparingUpdate && !IsDemo)
                _resetMessageWork = RunResetMessageCycleAsync();
        };
        RefreshResetMessageUi();
    }

    private static bool SupportsResetMessage(AccountSettings account) => account.Provider is ProviderKind.Claude or ProviderKind.Codex;
    private bool ResetMessageEnabled(AccountSettings account) => SupportsResetMessage(account) &&
        account.ResetMessageEnabled && _enabledResetAccounts.Contains(account.Id);

    private static string? FindResetMessageWindow(AccountSettings account, UsageSnapshot snapshot)
    {
        if (account.ResetMessageWindowId is { Length: > 0 } configured) return configured;
        return account.Provider switch
        {
            ProviderKind.Claude => snapshot.Windows.FirstOrDefault(window => window.Id.StartsWith("session::", StringComparison.Ordinal))?.Id,
            ProviderKind.Codex => snapshot.Windows.FirstOrDefault(window => window.Id == "codex:primary" || window.Id == "primary")?.Id,
            _ => null
        };
    }

    private int PauseResetMessagesForSettings(AccountSettings account)
    {
        _resetSettingsRevisions.TryGetValue(account.Id, out var previousRevision);
        var revision = _resetSettingsRevisions[account.Id] = previousRevision + 1;
        // Stop immediately when disabled. Enabling requires the preference to save first.
        if (!account.ResetMessageEnabled)
        {
            _enabledResetAccounts.Remove(account.Id);
            if (_resetMessageCancellations.TryGetValue(account.Id, out var pending)) pending.Cancel();
            _resetMessages.SynchronizeSettings(account, false, account.ResetMessageWindowId);
        }
        RefreshResetMessageUi();
        return revision;
    }

    private async Task ApplyResetMessageSettingsAsync(AccountSettings account, bool requestedEnabled, int revision)
    {
        if (!_resetSettingsRevisions.TryGetValue(account.Id, out var currentRevision) || currentRevision != revision ||
            account.ResetMessageEnabled != requestedEnabled) return;
        var wasEnabled = _enabledResetAccounts.Contains(account.Id);
        if (SupportsResetMessage(account) && account.ResetMessageEnabled) _enabledResetAccounts.Add(account.Id);
        else _enabledResetAccounts.Remove(account.Id);
        _resetMessages.SynchronizeSettings(account, ResetMessageEnabled(account), account.ResetMessageWindowId);
        await SaveResetMessagesAsync();
        _resetMessageNotices.Remove(account.Id);
        RefreshResetMessageUi();
        if (!wasEnabled && ResetMessageEnabled(account) && account.IdentityKey is not null)
            await _coordinator.RefreshAsync(account.Id, cancellationToken: _shutdown.Token);
    }

    private void RemoveResetMessageAccount(AccountSettings account)
    {
        _enabledResetAccounts.Remove(account.Id);
        if (_resetMessageCancellations.TryGetValue(account.Id, out var pending)) pending.Cancel();
        _resetMessages.RemoveAccount(account.Id);
        _resetMessageNotices.Remove(account.Id);
        _resetMessageRetryAt.Remove(account.Id);
    }

    private async Task ObserveResetMessageAsync(AccountSettings account, UsageSnapshot snapshot)
    {
        try
        {
            if (ResetMessageEnabled(account) && FindResetMessageWindow(account, snapshot) is { } target)
                account.ResetMessageWindowId = target;
            _resetMessages.Observe(account, ResetMessageEnabled(account), account.ResetMessageWindowId, snapshot, DateTimeOffset.UtcNow, isFresh: true);
            await SaveResetMessagesAsync();
            if (!_resetMessageCancellations.ContainsKey(account.Id) && snapshot.Windows.Any(window =>
                window.Id == account.ResetMessageWindowId && window.UsedPercent > 0 && window.ResetsAt > DateTimeOffset.UtcNow))
                _resetMessageNotices.Remove(account.Id);
        }
        catch (Exception error)
        {
            _resetMessageNotices[account.Id] = ("자동 메시지 · 저장 확인 필요", error.Message);
        }
        RefreshResetMessageUi();
    }

    private async Task SaveResetMessagesAsync()
    {
        if (IsDemo && _saveResetStateForVerification is null) return;
        await _resetStateSaveGate.WaitAsync(_shutdown.Token);
        try
        {
            var state = _resetMessages.GetState();
            var serialized = JsonSerializer.Serialize(state);
            if (serialized == _savedResetState && !_resetStateSaveFailed) return;
            if (IsDemo) await _saveResetStateForVerification!(state);
            else await _store.SaveResetMessageStateAsync(state, _shutdown.Token);
            _savedResetState = serialized;
            _resetStateSaveFailed = false;
        }
        catch { _resetStateSaveFailed = true; throw; }
        finally { _resetStateSaveGate.Release(); }
    }

    private async Task RunResetMessageCycleAsync()
    {
        try
        {
            foreach (var account in Settings.Accounts.ToArray())
            {
                if (_disposed || _preparingUpdate || _shutdown.IsCancellationRequested) break;
                if (_connections.ContainsKey(account.Id)) continue;
                var now = DateTimeOffset.UtcNow;
                var ticket = _resetMessages.GetDue(account, ResetMessageEnabled(account), account.ResetMessageWindowId, now);
                if (ticket is null || now < ticket.ResetsAt.AddSeconds(5) || _resetStateSaveFailed ||
                    _resetMessageRetryAt.TryGetValue(account.Id, out var retry) && now < retry) continue;
                await RunResetMessageAsync(account, ticket);
            }
            await SaveResetMessagesAsync();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            foreach (var account in Settings.Accounts.Where(ResetMessageEnabled))
                _resetMessageNotices[account.Id] = ("자동 메시지 · 확인 필요", error.Message);
        }
        finally { RefreshResetMessageUi(); }
    }

    private async Task RunResetMessageAsync(AccountSettings account, ResetMessageTicket ticket)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _resetMessageCancellations[account.Id] = cancel;
        var reserved = false;
        try
        {
            _resetMessageRetryAt[account.Id] = DateTimeOffset.UtcNow.AddMinutes(1);
            _resetMessageNotices[account.Id] = ("자동 메시지 · 사용량 확인 중", "초기화와 현재 계정을 확인한 뒤 한 번만 전송합니다.");
            RefreshResetMessageUi();
            var before = _coordinator.GetStatus(account.Id).Snapshot;
            var started = DateTimeOffset.UtcNow;
            await _coordinator.RefreshAsync(account.Id, cancellationToken: cancel.Token);
            var freshStatus = _coordinator.GetStatus(account.Id);
            var freshSnapshot = freshStatus.Snapshot;
            if (freshStatus.State != UsageState.Ready || freshStatus.IsRefreshing || freshSnapshot is null ||
                ReferenceEquals(before, freshSnapshot) || freshSnapshot.FetchedAt < started)
            {
                if (freshStatus.NextAttemptAt is { } next && next > _resetMessageRetryAt[account.Id]) _resetMessageRetryAt[account.Id] = next;
                _resetMessageNotices[account.Id] = ("자동 메시지 · 조회 대기", freshStatus.Message ?? "새 사용량을 확인한 뒤 다시 확인합니다.");
                return;
            }
            var sent = await _coordinator.SendResetMessageAsync(account.Id, ticket.IdentityKey,
                (status, token) => Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    token.ThrowIfCancellationRequested();
                    if (_preparingUpdate || _connections.ContainsKey(account.Id) || !ResetMessageEnabled(account)) return false;
                    _resetMessages.SynchronizeSettings(account, ResetMessageEnabled(account), account.ResetMessageWindowId);
                    if (!ReferenceEquals(status.Snapshot, freshSnapshot)) return false;
                    var decision = _resetMessages.PrepareAttempt(ticket, freshSnapshot, DateTimeOffset.UtcNow, isFresh: true);
                    reserved = decision == ResetMessageDecision.Reserved;
                    await SaveResetMessagesAsync(); // Never send if the durable reservation cannot be written.
                    token.ThrowIfCancellationRequested();
                    if (_preparingUpdate || _connections.ContainsKey(account.Id) || !ResetMessageEnabled(account) ||
                        !Settings.Accounts.Contains(account) || account.IdentityKey != ticket.IdentityKey ||
                        account.ResetMessageWindowId != ticket.WindowId) return false;
                    _resetMessageNotices[account.Id] = decision switch
                    {
                        ResetMessageDecision.Reserved => ("자동 메시지 · 전송 중", "고정된 짧은 메시지를 전송합니다. 같은 초기화에는 재전송하지 않습니다."),
                        ResetMessageDecision.AlreadyActive => ("자동 메시지 · 이미 시작됨", "새 사용 기간이 이미 시작되어 메시지를 보내지 않았습니다."),
                        _ => ("자동 메시지 · 초기화 확인 대기", "사용량이 0으로 초기화되고 다른 한도에 여유가 있는지 확인합니다.")
                    };
                    RefreshResetMessageUi();
                    return reserved;
                }).Task.Unwrap(), cancel.Token);
            if (reserved)
            {
                _resetMessages.CompleteAttempt(ticket, sent ? ResetMessageOutcome.Succeeded : ResetMessageOutcome.Unknown, DateTimeOffset.UtcNow);
                await SaveResetMessagesAsync();
                _resetMessageNotices[account.Id] = sent
                    ? ("자동 메시지 · 전송 완료", $"{DateTimeOffset.Now:MM.dd HH:mm:ss} 전송 완료. 사용 기간은 서비스가 결정하며 새 사용량으로 확인합니다.")
                    : ("자동 메시지 · 전송 중단", "설정이나 계정이 변경되어 중단했습니다. 중복 전송을 피하기 위해 재전송하지 않습니다.");
                if (sent) await _coordinator.RefreshAsync(account.Id, cancellationToken: cancel.Token);
            }
        }
        catch (Exception error)
        {
            if (reserved)
            {
                _resetMessages.CompleteAttempt(ticket, ResetMessageOutcome.Unknown, DateTimeOffset.UtcNow);
                try { await SaveResetMessagesAsync(); } catch { /* The reservation was already persisted; keep the send blocked. */ }
            }
            _resetMessageNotices[account.Id] = reserved
                ? ("자동 메시지 · 결과 확인 필요", "전송 결과를 확정하지 못했습니다. 같은 초기화에는 재전송하지 않습니다. " + error.Message)
                : ("자동 메시지 · 조회 대기", error is OperationCanceledException ? "자동 메시지가 중단되었습니다." : error.Message);
        }
        finally
        {
            _resetMessageCancellations.Remove(account.Id);
            RefreshResetMessageUi();
        }
    }

    private void RefreshResetMessageUi()
    {
        var state = _resetMessages.GetState();
        foreach (var vm in ViewModel.Accounts)
        {
            var account = vm.Settings;
            if (!account.ResetMessageEnabled) { vm.SetResetMessageStatus("자동 메시지 꺼짐", "세션 초기화 후 자동 전송하지 않습니다."); continue; }
            if (_resetMessageNotices.TryGetValue(account.Id, out var notice)) { vm.SetResetMessageStatus(notice.Label, notice.Detail); continue; }
            var observation = state.Observations.FirstOrDefault(item => item.AccountId == account.Id && item.IdentityKey == account.IdentityKey);
            var last = state.Attempts.LastOrDefault(item => item.Provider == account.Provider &&
                item.IdentityKey == account.IdentityKey && item.WindowId == account.ResetMessageWindowId);
            if (observation is not null)
                vm.SetResetMessageStatus($"자동 메시지 · {observation.ResetsAt.ToLocalTime():MM.dd HH:mm} 예정", "앱이 실행 중이고 초기화를 확인할 수 있을 때 전송합니다. 절전 등으로 1시간 이상 지났다면 건너뜁니다.");
            else if (last is not null)
                vm.SetResetMessageStatus(last.Outcome == ResetMessageOutcome.Succeeded ? "자동 메시지 · 전송 완료" : "자동 메시지 · 결과 확인 필요",
                    "같은 초기화에는 재전송하지 않습니다. 다음 사용 기간의 초기화 시각을 기다립니다.");
            else vm.SetResetMessageStatus("자동 메시지 · 다음 초기화 대기", "새 사용량 조회에서 세션 초기화 시각을 확인하면 예약합니다. 켜는 즉시 메시지를 보내지는 않습니다.");
        }
    }
}
