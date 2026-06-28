using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TokenWatch.Core.Models;

namespace TokenWatch.Core.Api;

/// <summary>Live Codex usage from the ChatGPT backend (more current than local logs).</summary>
public sealed record CodexLiveUsage(
    UsageWindow Primary,
    UsageWindow Secondary,
    DateTimeOffset ObservedAt,
    string? PlanType);

/// <summary>
/// Calls <c>https://chatgpt.com/backend-api/wham/usage</c> using the user's own
/// ChatGPT token from <c>~/.codex/auth.json</c> to read live 5h/weekly limit %.
/// Read-only GET of the user's own usage; returns null on any failure so callers
/// can fall back to the local session logs.
/// </summary>
public sealed class CodexUsageApiClient
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly string _authPath;

    public CodexUsageApiClient(string? authPath = null)
    {
        _authPath = authPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex", "auth.json");
    }

    public async Task<CodexLiveUsage?> TryGetAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_authPath)) return null;

        string token, account;
        try
        {
            using var auth = JsonDocument.Parse(await File.ReadAllTextAsync(_authPath, ct));
            var tokens = auth.RootElement.GetProperty("tokens");
            token = tokens.GetProperty("access_token").GetString() ?? "";
            account = tokens.TryGetProperty("account_id", out var a) ? a.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(token)) return null;
        }
        catch
        {
            return null;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrEmpty(account))
            req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account);
        req.Headers.UserAgent.ParseAdd("TokenWatch/0.1");
        req.Headers.Accept.ParseAdd("application/json");

        try
        {
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            if (!root.TryGetProperty("rate_limit", out var rl) || rl.ValueKind != JsonValueKind.Object)
                return null;

            var primary = ParseWindow(rl, "primary_window", WindowKind.FiveHour);
            var secondary = ParseWindow(rl, "secondary_window", WindowKind.Weekly);
            if (primary is null && secondary is null) return null;

            string? plan = root.TryGetProperty("plan_type", out var p) ? p.GetString() : null;

            return new CodexLiveUsage(
                primary ?? new UsageWindow(WindowKind.FiveHour, null, null),
                secondary ?? new UsageWindow(WindowKind.Weekly, null, null),
                DateTimeOffset.UtcNow,
                plan);
        }
        catch
        {
            return null;
        }
    }

    private static UsageWindow? ParseWindow(JsonElement rl, string name, WindowKind fallbackKind)
    {
        if (!rl.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object)
            return null;

        double? used = w.TryGetProperty("used_percent", out var up) && up.ValueKind is JsonValueKind.Number
            ? up.GetDouble() : null;

        DateTimeOffset? reset = w.TryGetProperty("reset_at", out var ra) && ra.ValueKind is JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64()) : null;

        var kind = fallbackKind;
        if (w.TryGetProperty("limit_window_seconds", out var ws) && ws.ValueKind is JsonValueKind.Number)
            kind = ws.GetInt64() <= 36000 ? WindowKind.FiveHour : WindowKind.Weekly; // <=10h → 5h window

        return new UsageWindow(kind, used, reset);
    }
}
