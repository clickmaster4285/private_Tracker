using client.Core.Models;

namespace client.Core.Abstractions;

public interface IActivityCollector
{
    Task<IReadOnlyList<ActivityLog>> CollectAsync(CancellationToken ct);
}
