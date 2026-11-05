namespace AlphaAITracker.Service;

/// <summary>
/// Live web-block snapshot shared by EnforceWorker, DnsBlockPinWorker, and the named pipe.
/// <see cref="Generation"/> tracks DomainBlockEnforcer apply epochs so pin writes cannot
/// race a clear after Inactive/delete.
/// </summary>
public sealed class WebBlockState
{
    private readonly object _gate = new();
    private IReadOnlyList<string> _domains = Array.Empty<string>();
    private long _policyVersion;
    private long _generation;
    private DateTimeOffset _updatedAt = DateTimeOffset.MinValue;

    public void Set(long policyVersion, IReadOnlyList<string> domains, long generation = 0)
    {
        lock (_gate)
        {
            _policyVersion = policyVersion;
            _generation = generation;
            _domains = domains.Count == 0
                ? Array.Empty<string>()
                : domains.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray();
            _updatedAt = DateTimeOffset.UtcNow;
        }
    }

    public (long PolicyVersion, IReadOnlyList<string> Domains, DateTimeOffset UpdatedAt, long Generation) Snapshot()
    {
        lock (_gate)
        {
            return (_policyVersion, _domains, _updatedAt, _generation);
        }
    }

    public IReadOnlyList<string> Domains
    {
        get { lock (_gate) return _domains; }
    }

    public long Generation
    {
        get { lock (_gate) return _generation; }
    }
}
