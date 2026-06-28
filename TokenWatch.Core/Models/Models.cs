namespace TokenWatch.Core.Models;

/// <summary>Which AI coding tool a snapshot belongs to.</summary>
public enum ProviderId
{
    ClaudeCode,
    Codex,
}

/// <summary>The rolling limit windows the providers expose.</summary>
public enum WindowKind
{
    FiveHour,
    Weekly,
}

/// <summary>
/// A single rate-limit window. <see cref="UsedPercent"/> is null when the
/// official percentage is not available locally (e.g. Claude Code, which only
/// exposes token counts in its logs — the real % needs the claude.ai API).
/// </summary>
public sealed record UsageWindow(
    WindowKind Kind,
    double? UsedPercent,
    DateTimeOffset? ResetsAt);

/// <summary>
/// Token counts. <see cref="Total"/> is supplied explicitly because the two
/// providers categorize tokens differently:
/// - Claude: Input/Output/CacheCreation/CacheRead are disjoint, Total is their sum.
/// - Codex:  Input already includes cached input, Output includes reasoning,
///           so Total = Input + Output and CacheRead is an informational subset.
/// </summary>
public sealed record TokenTotals(
    long Input,
    long Output,
    long CacheCreation,
    long CacheRead,
    long Total)
{
    public static TokenTotals Zero { get; } = new(0, 0, 0, 0, 0);

    public TokenTotals Add(TokenTotals o) => new(
        Input + o.Input,
        Output + o.Output,
        CacheCreation + o.CacheCreation,
        CacheRead + o.CacheRead,
        Total + o.Total);
}

/// <summary>A point-in-time reading for one provider.</summary>
public sealed record UsageSnapshot(
    ProviderId Provider,
    DateTimeOffset CapturedAt,
    IReadOnlyList<UsageWindow> Windows,
    TokenTotals TokensLast5h,
    TokenTotals TokensLast7d,
    TokenTotals TokensTotal,
    string? Note = null,
    // When the limit % values were actually observed. For Codex this is the
    // timestamp of the latest local rate_limits event, which may be stale if
    // the tool has not run recently. Null when limit % is unavailable.
    DateTimeOffset? LimitObservedAt = null)
{
    public UsageWindow? Window(WindowKind kind) =>
        Windows.FirstOrDefault(w => w.Kind == kind);
}
