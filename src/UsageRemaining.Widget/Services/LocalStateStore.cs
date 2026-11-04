using System.IO;
using System.Text.Json;
using UsageRemaining.Widget.Models;

namespace UsageRemaining.Widget.Services;

public sealed class LocalStateStore
{
    private readonly string path;
    private readonly JsonSerializerOptions options = new() { WriteIndented = true };
    public string? Warning { get; private set; }

    public LocalStateStore(string? directory = null)
    {
        directory ??= Environment.GetEnvironmentVariable("USAGEREMAINING_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexPulse");
        path = Path.Combine(directory, "widget-state.json");
    }

    public WidgetState Load()
    {
        try
        {
            if (!File.Exists(path)) return new();
            var state = JsonSerializer.Deserialize<WidgetState>(File.ReadAllText(path), options) ?? new();
            state.Opacity = double.IsFinite(state.Opacity) ? Math.Clamp(state.Opacity, .55, 1) : 1;
            state.Accounts ??= [];
            state.PendingResetUses ??= [];
            foreach (var key in state.PendingResetUses.Keys.ToList())
                if (state.PendingResetUses[key] is not { } operation || string.IsNullOrWhiteSpace(operation.CreditId) || string.IsNullOrWhiteSpace(operation.IdempotencyKey))
                    state.PendingResetUses.Remove(key);
            if (state.PublicCalendar is { } calendar && (calendar.Source != PublicResetCalendar.ProviderId
                || calendar.Events is null || calendar.Events.Any(e => e is null)))
                state.PublicCalendar = null;
            foreach (var key in state.Accounts.Keys.ToList())
            {
                var account = state.Accounts[key];
                if (account is null) { state.Accounts.Remove(key); continue; }
                account.Resets ??= [];
                account.Resets.RemoveAll(r => r is null);
                if (account.LastSnapshot is { } snapshot &&
                    (snapshot.Windows is null || snapshot.Windows.Any(w => w is null || !double.IsFinite(w.UsedPercent))))
                    account.LastSnapshot = null;
            }
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Warning = "저장 기록을 읽지 못했습니다. 새 기록으로 시작합니다.";
            return new();
        }
    }

    public bool Save(WidgetState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(state, options));
            File.Move(tempPath, path, overwrite: true);
            Warning = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Warning = "기록을 저장하지 못했습니다. 폴더 권한을 확인하세요.";
            return false;
        }
    }
}
