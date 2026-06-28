using System.Text.Json;
using TokenWatch.Core.Models;

namespace TokenWatch.Core.Collectors;

/// <summary>
/// Reads Claude Code token usage from <c>~/.claude/projects/&lt;proj&gt;/&lt;session&gt;.jsonl</c>.
/// Each assistant record carries <c>message.usage</c> with input/output/cache token
/// counts and a top-level ISO <c>timestamp</c>. Duplicate records (resumed/copied
/// sessions) are de-duplicated by <c>message.id + requestId</c>, matching ccusage.
///
/// The official 5h/weekly limit % is NOT in these logs — it requires the claude.ai
/// API (a later milestone), so the limit windows are returned with null percentages.
/// </summary>
public sealed class ClaudeCollector : IUsageCollector
{
    private readonly string _projectsRoot;

    public ClaudeCollector(string? projectsRoot = null)
    {
        _projectsRoot = projectsRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");
    }

    public ProviderId Provider => ProviderId.ClaudeCode;

    public Task<UsageSnapshot> CollectAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff5h = now.AddHours(-5);
        var cutoff7d = now.AddDays(-7);

        var totals5h = TokenTotals.Zero;
        var totals7d = TokenTotals.Zero;
        var totalsAll = TokenTotals.Zero;

        var seen = new HashSet<string>();

        if (Directory.Exists(_projectsRoot))
        {
            foreach (var file in Directory.EnumerateFiles(_projectsRoot, "*.jsonl", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var (ts, dedupKey, usage) in ReadUsage(file, ct))
                {
                    if (dedupKey is not null && !seen.Add(dedupKey)) continue;

                    totalsAll = totalsAll.Add(usage);
                    if (ts >= cutoff7d) totals7d = totals7d.Add(usage);
                    if (ts >= cutoff5h) totals5h = totals5h.Add(usage);
                }
            }
        }

        var windows = new List<UsageWindow>
        {
            new(WindowKind.FiveHour, null, null),
            new(WindowKind.Weekly, null, null),
        };

        var snapshot = new UsageSnapshot(
            ProviderId.ClaudeCode, now, windows, totals5h, totals7d, totalsAll,
            Note: "公式 limit% は claude.ai API 連携で取得予定（現在はトークン量のみ）");
        return Task.FromResult(snapshot);
    }

    private static IEnumerable<(DateTimeOffset Ts, string? DedupKey, TokenTotals Usage)> ReadUsage(
        string path, CancellationToken ct)
    {
        IEnumerable<string> lines;
        try { lines = File.ReadLines(path); }
        catch (IOException) { yield break; }

        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;

            (DateTimeOffset, string?, TokenTotals)? parsed;
            try { parsed = ParseLine(line); }
            catch (JsonException) { continue; }

            if (parsed is { } p) yield return p;
        }
    }

    private static (DateTimeOffset, string?, TokenTotals)? ParseLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
            return null;
        if (!msg.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        long input = GetLong(usage, "input_tokens");
        long output = GetLong(usage, "output_tokens");
        long cacheCreate = GetLong(usage, "cache_creation_input_tokens");
        long cacheRead = GetLong(usage, "cache_read_input_tokens");
        long total = input + output + cacheCreate + cacheRead;
        if (total == 0) return null;

        var ts = root.TryGetProperty("timestamp", out var tsEl)
            && DateTimeOffset.TryParse(tsEl.GetString(), out var parsedTs)
            ? parsedTs.ToUniversalTime()
            : DateTimeOffset.UtcNow;

        string? dedupKey = null;
        var msgId = msg.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var reqId = root.TryGetProperty("requestId", out var rEl) ? rEl.GetString() : null;
        if (msgId is not null || reqId is not null) dedupKey = $"{msgId}:{reqId}";

        return (ts, dedupKey, new TokenTotals(input, output, cacheCreate, cacheRead, total));
    }

    private static long GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number ? v.GetInt64() : 0;
}
