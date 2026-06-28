using System.Text.Json;
using TokenWatch.Core.Models;

namespace TokenWatch.Core.Api;

/// <summary>Live Claude limit % parsed from the claude.ai usage API.</summary>
public sealed record ClaudeLiveUsage(
    UsageWindow FiveHour,
    UsageWindow Weekly,
    DateTimeOffset ObservedAt);

/// <summary>
/// Parses the claude.ai <c>/api/organizations/{org}/usage</c> response. Verified
/// shape (2026-06): top-level <c>five_hour</c>/<c>seven_day</c> objects each with
/// <c>utilization</c> (0–100) and ISO <c>resets_at</c>, plus a <c>limits</c> array
/// (<c>group</c> = "session" | "weekly", <c>percent</c>, <c>resets_at</c>). The
/// named objects are preferred; the limits array is the fallback.
/// </summary>
public static class ClaudeUsageParser
{
    public static ClaudeLiveUsage? TryParse(string rawJson, DateTimeOffset now)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(rawJson); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var five = ReadNamed(root, "five_hour", WindowKind.FiveHour)
                       ?? ReadFromLimits(root, "session", WindowKind.FiveHour);
            var week = ReadNamed(root, "seven_day", WindowKind.Weekly)
                       ?? ReadFromLimits(root, "weekly", WindowKind.Weekly);

            if (five is null && week is null) return null;

            return new ClaudeLiveUsage(
                five ?? new UsageWindow(WindowKind.FiveHour, null, null),
                week ?? new UsageWindow(WindowKind.Weekly, null, null),
                now);
        }
    }

    private static UsageWindow? ReadNamed(JsonElement root, string name, WindowKind kind)
    {
        if (!root.TryGetProperty(name, out var o) || o.ValueKind != JsonValueKind.Object)
            return null;
        if (!o.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number)
            return null;
        return new UsageWindow(kind, u.GetDouble(), ReadReset(o));
    }

    private static UsageWindow? ReadFromLimits(JsonElement root, string group, WindowKind kind)
    {
        if (!root.TryGetProperty("limits", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var g = item.TryGetProperty("group", out var gv) ? gv.GetString() : null;
            var k = item.TryGetProperty("kind", out var kv) ? kv.GetString() : null;
            if (!Matches(g, group) && !Matches(k, group)) continue;
            if (!item.TryGetProperty("percent", out var p) || p.ValueKind != JsonValueKind.Number) continue;
            return new UsageWindow(kind, p.GetDouble(), ReadReset(item));
        }
        return null;
    }

    private static bool Matches(string? value, string group) =>
        value is not null && value.StartsWith(group, StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? ReadReset(JsonElement o) =>
        o.TryGetProperty("resets_at", out var r)
        && r.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(r.GetString(), out var dt)
            ? dt
            : null;
}
