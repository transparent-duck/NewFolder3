using System.Security.Cryptography;
using System.Text.Json;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private sealed record ProgressionCheckpoint(ulong Character, FarmingPlan Plan, SaveSlotSnapshot Slot,
        int CompletedCycles, int Failures, string SettingsFingerprint, string? InvalidSourceReason = null);

    private readonly string _checkpointPath;
    private ProgressionCheckpoint? _checkpoint;
    private (string Message, bool CanReplace)? _checkpointIssue;

    private bool ProgressionPauseLocked => _checkpoint != null ||
        _ddHost?.ActiveFarming?.Session.PersistPause != null;

    private bool CanRequestProgressionPause => !ProgressionPauseLocked &&
        _ddHost?.ActiveFarming?.CanRequestPause == true;

    private void LoadProgressionCheckpoint()
    {
        try
        {
            var checkpoint = ReadProgressionCheckpoint();
            if (checkpoint.InvalidSourceReason is { } reason)
            {
                _checkpointIssue = (reason, true);
                return;
            }
            _checkpoint = checkpoint;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex)
        {
            _checkpointIssue = ($"無法讀取暫停紀錄：{ex.Message}",
                ex is JsonException or InvalidDataException);
            Service.Log.Error(ex, "[FSD] Progression checkpoint read failed");
        }
    }

    private ProgressionCheckpoint ReadProgressionCheckpoint()
    {
        var checkpoint = JsonSerializer.Deserialize<ProgressionCheckpoint>(File.ReadAllBytes(_checkpointPath));
        if (checkpoint is not { Character: > 0, CompletedCycles: >= 0, Failures: >= 0,
                Plan: { Mode: FarmingMode.DeepProgression, SaveUse: SaveUse.Create, DungeonId: 1 or 2 or 4,
                    TargetCycles: > 0, HoldAfterClear: false, StopAfterFloor: null },
                Slot: { Index: >= 0, Empty: false, Enterable: true, RowFingerprint.Length: 64 },
                SettingsFingerprint.Length: 64 } ||
            checkpoint.Slot.StartFloor <= checkpoint.Plan.StartFloor ||
            checkpoint.Slot.StartFloor > checkpoint.Plan.TerminalEntryFloor ||
            checkpoint.Slot.StartFloor % 10 != 1 ||
            !checkpoint.Plan.Infinite && checkpoint.CompletedCycles >= checkpoint.Plan.TargetCycles)
            throw new InvalidDataException("暫停紀錄內容無效。");
        return checkpoint;
    }

    private void WriteProgressionCheckpoint(ProgressionCheckpoint checkpoint, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_checkpointPath)!);
        var temporary = _checkpointPath + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(checkpoint));
        File.Move(temporary, _checkpointPath, overwrite);
    }

    private string SettingsFingerprint() =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(_configuration)));

    private void RequestProgressionPause()
    {
        if (!CanRequestProgressionPause || _ddHost is not { ActiveFarming: { } farming } host) return;
        ulong character = Service.PlayerState.ContentId;
        if (character == 0)
        {
            _farmingError = "無法辨認目前角色，請稍後再要求暫停。";
            return;
        }
        var session = farming.Session;
        var plan = session.Plan with
        {
            TargetCycles = host.TargetLoops, Infinite = host.Infinite,
            BandedEnabled = session.BandedEnabled, OpenGold = session.OpenGold,
            OpenSilver = session.OpenSilver, OpenBronze = session.OpenBronze
        };
        var completedCycles = host.CompletedLoops;
        var settings = SettingsFingerprint();
        session.PersistPause = slot => PersistProgressionCheckpoint(
            new(character, plan, slot, completedCycles, session.Failures, settings));
        _farmingError = null;
    }

    private string? PersistProgressionCheckpoint(ProgressionCheckpoint checkpoint)
    {
        if (Service.PlayerState.ContentId != checkpoint.Character || checkpoint.Slot.RowFingerprint is not { Length: 64 })
            return "無法驗證目前角色與存檔資料，暫停紀錄未寫入。";

        _checkpoint = checkpoint;
        try
        {
            // A new pause can only be requested when no resumable checkpoint is loaded.
            WriteProgressionCheckpoint(checkpoint, overwrite: _checkpointIssue?.CanReplace == true);
            _checkpointIssue = null;
            return null;
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, "[FSD] Progression checkpoint write failed");
            return "寫入暫停紀錄失敗。本次仍可嘗試繼續，重新載入前請先檢查日誌。";
        }
    }

    private FsdControlResult ResumeProgression()
    {
        if (_checkpoint is not { } checkpoint)
            return new(false, _checkpointIssue?.Message ?? "沒有暫停中的死靈術士任務。");
        if (_currentInDeepDungeon || _dutyState?.IsInDuty == true || DutyState.IsClientTransitioning)
            return new(false, "請在副本外、過圖完成後繼續。");
        if (Service.LocalPlayer is not { IsDead: false } player || Service.PlayerState.ContentId != checkpoint.Character)
            return new(false, "請使用暫停任務時的角色繼續。");
        if (checkpoint.Plan.DungeonId is 1 or 2 && player.ClassJob.RowId is not (23 or 31 or 38))
            return new(false, "死宮／天宮死靈術士模式僅支援機工士、吟遊詩人、舞者。");
        if (SettingsFingerprint() != checkpoint.SettingsFingerprint)
            return new(false, "FSD 設定與暫停時不同。請還原原本的設定後重試。");

        var session = new FarmingSession(checkpoint.Plan);
        session.RestoreCheckpoint(checkpoint.Slot, checkpoint.Failures, checkpoint.Character, ConfirmProgressionResume,
            reason => InvalidateProgressionSource(checkpoint, reason));
        var started = TryStartOutsideDutyFsd(() => new FarmingScenario(session),
            checkpoint.Plan.TargetCycles, checkpoint.Plan.Infinite, null, out var error,
            resumePausedProgression: true, completedLoops: checkpoint.CompletedCycles);
        return new(started, error);
    }

    private void InvalidateProgressionSource(ProgressionCheckpoint checkpoint, string reason)
    {
        if (_checkpoint != checkpoint || Service.PlayerState.ContentId != checkpoint.Character)
            return;
        _checkpoint = null;
        string issue = $"原任務不可繼續：{reason}";
        _checkpointIssue = (issue, false);
        try
        {
            if (File.Exists(_checkpointPath))
            {
                var contents = File.ReadAllBytes(_checkpointPath);
                if (JsonSerializer.Deserialize<ProgressionCheckpoint>(contents) != checkpoint)
                    throw new InvalidDataException("本機暫停紀錄已由其他操作變更。");
                WriteProgressionCheckpoint(checkpoint with { InvalidSourceReason = issue }, overwrite: true);
                _checkpointIssue = (issue, true);
            }
        }
        catch (Exception ex)
        {
            _checkpointIssue = ($"{issue} 暫停紀錄更新失敗：{ex.Message}", false);
            Service.Log.Error(ex, "[FSD] Could not mark the checkpoint source invalid.");
        }
    }

    private string? ConfirmProgressionResume()
    {
        if (_checkpoint is not { } checkpoint || Service.PlayerState.ContentId != checkpoint.Character ||
            SettingsFingerprint() != checkpoint.SettingsFingerprint)
            return "入場期間角色或 FSD 設定已變更，請檢查暫停紀錄。";

        try
        {
            if (File.Exists(_checkpointPath) &&
                JsonSerializer.Deserialize<ProgressionCheckpoint>(File.ReadAllBytes(_checkpointPath)) != checkpoint)
                return "入場期間本機暫停紀錄已變更，請檢查後重試。";
            File.Delete(_checkpointPath);
            _checkpoint = null;
            _checkpointIssue = null;
            return null;
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, "[FSD] Progression checkpoint could not be consumed");
            return "已入場，但無法清除暫停紀錄。FSD 已停止。";
        }
    }
}
