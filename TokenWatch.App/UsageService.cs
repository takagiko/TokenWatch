using TokenWatch.Core.Api;
using TokenWatch.Core.Collectors;
using TokenWatch.Core.Models;

namespace TokenWatch.App;

/// <summary>
/// Runs all collectors off the UI thread and, when a <see cref="ClaudeUsageService"/>
/// is supplied, augments the Claude snapshot with live limit % from claude.ai.
/// </summary>
public sealed class UsageService
{
    private readonly IUsageCollector[] _collectors =
    [
        new ClaudeCollector(),
        new CodexCollector(),
    ];

    private readonly ClaudeUsageService? _claude;

    public UsageService(ClaudeUsageService? claude = null)
    {
        _claude = claude;
    }

    public async Task<IReadOnlyList<UsageSnapshot>> RefreshAsync(CancellationToken ct = default)
    {
        var results = new List<UsageSnapshot>(_collectors.Length);
        foreach (var c in _collectors)
        {
            try
            {
                results.Add(await Task.Run(() => c.CollectAsync(ct), ct));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // One provider failing should not break the others.
            }
        }

        // Live Claude limit % (UI thread — WebView2). Runs after the collectors so
        // the continuation is back on the captured UI context.
        if (_claude is { HasOrg: true })
        {
            try
            {
                var live = await _claude.TryGetAsync(ct);
                if (live is not null) ApplyClaudeLive(results, live);
            }
            catch
            {
                // Keep token-only Claude data on any failure.
            }
        }

        return results;
    }

    private static void ApplyClaudeLive(List<UsageSnapshot> results, ClaudeLiveUsage live)
    {
        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].Provider != ProviderId.ClaudeCode) continue;
            results[i] = results[i] with
            {
                Windows = [live.FiveHour, live.Weekly],
                Note = "ライブ (claude.ai)",
                LimitObservedAt = live.ObservedAt,
            };
        }
    }
}
