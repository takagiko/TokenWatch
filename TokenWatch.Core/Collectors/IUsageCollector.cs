using TokenWatch.Core.Models;

namespace TokenWatch.Core.Collectors;

/// <summary>
/// Produces a <see cref="UsageSnapshot"/> for one provider. Implementations read
/// local log files (and, later, provider APIs for official limit %).
/// </summary>
public interface IUsageCollector
{
    ProviderId Provider { get; }

    Task<UsageSnapshot> CollectAsync(CancellationToken ct = default);
}
