using System.Collections.Concurrent;
using System.Collections.Generic;
using LandingStats.Core;

// Historical fixture only. Production persistence is owned by the runtime.
namespace LandingStats.App.Storage;

internal sealed class BoundedTelemetryQueue : System.IDisposable
{
    private readonly BlockingCollection<TelemetrySample> _samples;

    public BoundedTelemetryQueue(int capacity)
    {
        _samples = new BlockingCollection<TelemetrySample>(
            new ConcurrentQueue<TelemetrySample>(),
            capacity);
    }

    public bool TryAdd(TelemetrySample sample)
    {
        return _samples.TryAdd(sample);
    }

    public void CompleteAdding()
    {
        _samples.CompleteAdding();
    }

    public IEnumerable<TelemetrySample> GetConsumingEnumerable()
    {
        return _samples.GetConsumingEnumerable();
    }

    public void Dispose()
    {
        _samples.Dispose();
    }
}
