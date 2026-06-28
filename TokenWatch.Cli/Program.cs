using System.Globalization;
using TokenWatch.Core.Collectors;
using TokenWatch.Core.Models;

Console.OutputEncoding = System.Text.Encoding.UTF8;

IUsageCollector[] collectors =
[
    new ClaudeCollector(),
    new CodexCollector(),
];

Console.WriteLine("TokenWatch — 使用量チェック (v1 / ローカル読み)");
Console.WriteLine($"取得時刻: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine(new string('=', 56));

foreach (var collector in collectors)
{
    UsageSnapshot snap;
    try
    {
        snap = await collector.CollectAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n[{collector.Provider}] 取得失敗: {ex.Message}");
        continue;
    }

    PrintSnapshot(snap);
}

static void PrintSnapshot(UsageSnapshot s)
{
    var title = s.Provider == ProviderId.ClaudeCode ? "Claude Code" : "Codex";
    Console.WriteLine();
    Console.WriteLine($"■ {title}");

    var now = DateTimeOffset.UtcNow;
    foreach (var kind in new[] { WindowKind.FiveHour, WindowKind.Weekly })
    {
        var w = s.Window(kind);
        var label = kind == WindowKind.FiveHour ? "5時間枠" : "週間枠 ";
        if (w?.UsedPercent is { } pct)
        {
            bool reset = w.ResetsAt is { } rr && rr <= now;
            var resetStr = w.ResetsAt is { } r
                ? $" / リセット {r.ToLocalTime():MM-dd HH:mm}"
                : "";
            var flag = reset ? "  ⚠ リセット済(古い値)" : "";
            Console.WriteLine($"  {label}: {Bar(pct)} {pct,5:0.0}%{resetStr}{flag}");
        }
        else
        {
            Console.WriteLine($"  {label}: (limit% 未取得)");
        }
    }

    if (s.LimitObservedAt is { } obs)
    {
        var age = now - obs;
        var ageStr = age.TotalHours < 1 ? $"{age.TotalMinutes:0}分前"
            : age.TotalDays < 1 ? $"{age.TotalHours:0.0}時間前"
            : $"{age.TotalDays:0.0}日前";
        var fresh = age.TotalHours <= 5 ? "" : "  ← 最新のCodex実行が古いため limit% は参考値";
        Console.WriteLine($"  limit観測: {obs.ToLocalTime():MM-dd HH:mm} ({ageStr}){fresh}");
    }

    Console.WriteLine($"  トークン  5h: {FmtTokens(s.TokensLast5h)}");
    Console.WriteLine($"           7d: {FmtTokens(s.TokensLast7d)}");
    Console.WriteLine($"          累計: {FmtTokens(s.TokensTotal)}");

    if (s.Note is not null)
        Console.WriteLine($"  注: {s.Note}");
}

static string FmtTokens(TokenTotals t) =>
    $"{Group(t.Total)} tok (in {Group(t.Input)} / out {Group(t.Output)} / cache {Group(t.CacheCreation + t.CacheRead)})";

static string Group(long n) => n.ToString("#,0", CultureInfo.InvariantCulture);

static string Bar(double pct)
{
    const int width = 20;
    var filled = (int)Math.Round(Math.Clamp(pct, 0, 100) / 100.0 * width);
    return "[" + new string('#', filled) + new string('-', width - filled) + "]";
}
