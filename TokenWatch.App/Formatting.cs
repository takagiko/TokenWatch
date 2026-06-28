using TokenWatch.Core.Models;

namespace TokenWatch.App;

/// <summary>Severity used to color icons, bars and text consistently.</summary>
public enum Severity { Unknown, Ok, Warn, Critical, Stale }

public static class Formatting
{
    /// <summary>Severity thresholds (0–100), configurable via settings.</summary>
    public static int WarnPercent { get; set; } = 50;
    public static int CriticalPercent { get; set; } = 80;

    /// <summary>Whether a limit reading is too old to trust the percentage.</summary>
    public static bool IsStale(UsageSnapshot s, UsageWindow w, DateTimeOffset now)
    {
        if (w.ResetsAt is { } r && r <= now) return true;
        if (s.LimitObservedAt is { } obs)
        {
            var maxAge = w.Kind == WindowKind.FiveHour ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7);
            return now - obs > maxAge;
        }
        return false;
    }

    public static Severity ForPercent(double? pct, bool stale)
    {
        if (pct is null) return Severity.Unknown;
        if (stale) return Severity.Stale;
        return pct >= CriticalPercent ? Severity.Critical : pct >= WarnPercent ? Severity.Warn : Severity.Ok;
    }

    /// <summary>1,630,214 → "1.63M".</summary>
    public static string Compact(long n) => n switch
    {
        >= 1_000_000_000 => (n / 1e9).ToString("0.##") + "B",
        >= 1_000_000 => (n / 1e6).ToString("0.##") + "M",
        >= 1_000 => (n / 1e3).ToString("0.##") + "K",
        _ => n.ToString(),
    };

    public static string TokenLine(TokenTotals t) =>
        $"{Compact(t.Total)} tok  (in {Compact(t.Input)} / out {Compact(t.Output)} / cache {Compact(t.CacheCreation + t.CacheRead)})";

    public static string Age(DateTimeOffset from, DateTimeOffset now)
    {
        var d = now - from;
        return d.TotalMinutes < 1 ? "たった今"
            : d.TotalHours < 1 ? $"{d.TotalMinutes:0}分前"
            : d.TotalDays < 1 ? $"{d.TotalHours:0.0}時間前"
            : $"{d.TotalDays:0.0}日前";
    }

    public static string ProviderName(ProviderId p) =>
        p == ProviderId.ClaudeCode ? "Claude Code" : "Codex";
}
