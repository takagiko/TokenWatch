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
            // Scan every rollout file. We can't filter by file mtime: a long-running
            // session is appended in place, and Windows reports a stale last-write
            // time for a file that is still held open — so an mtime filter would skip
            // exactly the active session. Reading is cheap because ReadTokenEvents
            // only JSON-parses lines that contain a token_count event.
            foreach (var path in Directory.EnumerateFiles(_sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var (ts, usage, rl) in ReadTokenEvents(path, ct))
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
        // FileShare.ReadWrite so we can read a session file Codex is still appending to.
        StreamReader reader;
        try
        {
            reader = new StreamReader(new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        using (reader)
        {
            while (true)
            {
                string? line = SafeReadLine(reader);
                if (line is null) break; // EOF or read error

                ct.ThrowIfCancellationRequested();
                // Fast path: only token_count events carry usage and rate_limits,
                // so skip the (large) conversation-content lines without parsing JSON.
                if (line.Length == 0 || !line.Contains("token_count", StringComparison.Ordinal))
                    continue;

                (DateTimeOffset, TokenTotals?, RateLimits?)? parsed;
                try { parsed = ParseLine(line); }
                catch (JsonException) { continue; }
                catch (InvalidOperationException) { continue; } // unexpected null/shape

                if (parsed is { } p) yield return p;
            }
        }
    }

    private static string? SafeReadLine(StreamReader reader)
    {
        try { return reader.ReadLine(); }
        catch (IOException) { return null; }
    }

    private static (DateTimeOffset, TokenTotals?, RateLimits?)? ParseLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return null;
        if (!payload.TryGetProperty("type", out var pt) || pt.GetString() != "token_count")
            return null;

        var ts = root.TryGetProperty("timestamp", out var tsEl)
            && DateTimeOffset.TryParse(tsEl.GetString(), out var parsedTs)
            ? parsedTs.ToUniversalTime()
            : DateTimeOffset.UtcNow;

        TokenTotals? usage = null;
        if (payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object)
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
