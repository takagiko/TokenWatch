using System.Text.Json;
using TokenWatch.Core.Api;
using TokenWatch.Core.Models;

namespace TokenWatch.Core.Collectors;

/// <summary>
/// Reads Codex usage. Live 5h/weekly limit % comes from the ChatGPT API
/// (<see cref="CodexUsageApiClient"/>); token totals and a fallback limit %
/// come from <c>~/.codex/sessions/.../rollout-*.jsonl</c> (whose
/// <c>token_count</c> events carry per-turn usage and the last-seen rate_limits,
/// which may be stale when Codex has not run recently).
/// </summary>
public sealed class CodexCollector : IUsageCollector
{
    private readonly string _sessionsRoot;
    private readonly CodexUsageApiClient? _api;

    public CodexCollector(string? sessionsRoot = null, CodexUsageApiClient? api = null)
    {
        _sessionsRoot = sessionsRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex", "sessions");
        _api = api ?? new CodexUsageApiClient();
    }

    public ProviderId Provider => ProviderId.Codex;

    public async Task<UsageSnapshot> CollectAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff5h = now.AddHours(-5);
        var cutoff7d = now.AddDays(-7);

        var totals5h = TokenTotals.Zero;
        var totals7d = TokenTotals.Zero;
        var totalsAll = TokenTotals.Zero;

        DateTimeOffset latestEventAt = DateTimeOffset.MinValue;
        UsageWindow? primary = null;
        UsageWindow? secondary = null;

        if (Directory.Exists(_sessionsRoot))
        {
            // Newest files first so the very first rate_limits we see is current.
            var files = Directory.EnumerateFiles(_sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            // Scan files touched within the weekly window, plus always the newest
            // file (for current rate_limits even if the account has been idle).
            var weekAgo = now.AddDays(-8); // small margin over 7d
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                bool isNewest = ReferenceEquals(file, files[0]);
                if (!isNewest && file.LastWriteTimeUtc < weekAgo.UtcDateTime)
                    break; // older than we care about; list is sorted desc

                foreach (var (ts, usage, rl) in ReadTokenEvents(file.FullName, ct))
                {
                    if (usage is not null)
                    {
                        totalsAll = totalsAll.Add(usage);
                        if (ts >= cutoff7d) totals7d = totals7d.Add(usage);
                        if (ts >= cutoff5h) totals5h = totals5h.Add(usage);
                    }

                    if (rl is not null && ts > latestEventAt)
                    {
                        latestEventAt = ts;
                        primary = rl.Value.Primary;
                        secondary = rl.Value.Secondary;
                    }
                }
            }
        }

        // Prefer live limit % from the ChatGPT API; fall back to local logs.
        if (_api is not null)
        {
            var live = await _api.TryGetAsync(ct);
            if (live is not null)
            {
                return new UsageSnapshot(
                    ProviderId.Codex, now,
                    [live.Primary, live.Secondary],
                    totals5h, totals7d, totalsAll,
                    Note: "ライブ (chatgpt.com API)",
                    LimitObservedAt: live.ObservedAt);
            }
        }

        var windows = new List<UsageWindow>
        {
            primary   ?? new UsageWindow(WindowKind.FiveHour, null, null),
            secondary ?? new UsageWindow(WindowKind.Weekly,   null, null),
        };

        var note = primary is null
            ? "rate_limits イベントが見つかりません（API も利用不可）"
            : "ローカルログ由来（API 取得不可のため古い可能性）";

        return new UsageSnapshot(
            ProviderId.Codex, now, windows, totals5h, totals7d, totalsAll, note,
            LimitObservedAt: primary is null ? null : latestEventAt);
    }

    private readonly record struct RateLimits(UsageWindow Primary, UsageWindow Secondary);

    private static IEnumerable<(DateTimeOffset Ts, TokenTotals? Usage, RateLimits? Rl)> ReadTokenEvents(
        string path, CancellationToken ct)
    {
        IEnumerable<string> lines;
        try { lines = File.ReadLines(path); }
        catch (IOException) { yield break; }

        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;

            (DateTimeOffset, TokenTotals?, RateLimits?)? parsed;
            try { parsed = ParseLine(line); }
            catch (JsonException) { continue; }

            if (parsed is { } p) yield return p;
        }
    }

    private static (DateTimeOffset, TokenTotals?, RateLimits?)? ParseLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        if (!root.TryGetProperty("payload", out var payload)) return null;
        if (!payload.TryGetProperty("type", out var pt) || pt.GetString() != "token_count")
            return null;

        var ts = root.TryGetProperty("timestamp", out var tsEl)
            && DateTimeOffset.TryParse(tsEl.GetString(), out var parsedTs)
            ? parsedTs.ToUniversalTime()
            : DateTimeOffset.UtcNow;

        TokenTotals? usage = null;
        if (payload.TryGetProperty("info", out var info)
            && info.TryGetProperty("last_token_usage", out var last))
        {
            long input = GetLong(last, "input_tokens");
            long output = GetLong(last, "output_tokens");
            long cached = GetLong(last, "cached_input_tokens");
            long total = GetLong(last, "total_tokens");
            if (total == 0) total = input + output;
            // Codex: input already includes cached; output includes reasoning.
            usage = new TokenTotals(input, output, 0, cached, total);
        }

        RateLimits? rl = null;
        if (payload.TryGetProperty("rate_limits", out var rlEl) && rlEl.ValueKind == JsonValueKind.Object)
        {
            var primary = ParseWindow(rlEl, "primary", WindowKind.FiveHour);
            var secondary = ParseWindow(rlEl, "secondary", WindowKind.Weekly);
            if (primary is not null || secondary is not null)
            {
                rl = new RateLimits(
                    primary ?? new UsageWindow(WindowKind.FiveHour, null, null),
                    secondary ?? new UsageWindow(WindowKind.Weekly, null, null));
            }
        }

        if (usage is null && rl is null) return null;
        return (ts, usage, rl);
    }

    private static UsageWindow? ParseWindow(JsonElement rlEl, string name, WindowKind fallbackKind)
    {
        if (!rlEl.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object)
            return null;

        double? used = w.TryGetProperty("used_percent", out var up)
            && up.ValueKind is JsonValueKind.Number ? up.GetDouble() : null;

        DateTimeOffset? resets = w.TryGetProperty("resets_at", out var ra)
            && ra.ValueKind is JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64())
            : null;

        // Prefer window_minutes to classify, fall back to position.
        var kind = fallbackKind;
        if (w.TryGetProperty("window_minutes", out var wm) && wm.ValueKind is JsonValueKind.Number)
            kind = wm.GetInt32() <= 600 ? WindowKind.FiveHour : WindowKind.Weekly;

        return new UsageWindow(kind, used, resets);
    }

    private static long GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number ? v.GetInt64() : 0;
}
