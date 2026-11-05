namespace client.Core.DesktopEventBus;

public interface IObservableEventSource
{
    string SourceName { get; }
    bool IsActive { get; }
    event EventHandler<RawDesktopEvent>? EventRaised;
    Task StartAsync(CancellationToken ct);
    void Stop();
}
