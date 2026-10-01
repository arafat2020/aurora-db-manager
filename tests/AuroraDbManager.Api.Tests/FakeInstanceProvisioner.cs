using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Tests;

/// <summary>
/// Provisioner whose outcome and timing are scripted by the test: calls can be made to fail, and
/// can be held at a gate so a test can observe the system while a call is in flight.
/// </summary>
public sealed class FakeInstanceProvisioner : IInstanceProvisioner
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _started = new(0);
    private readonly SemaphoreSlim _gate = new(0);
    private volatile bool _blocking;
    private int _failuresRemaining;
    private Exception? _failure;
    private int _inFlight;

    public int CallCount { get; private set; }

    public List<Guid> DeprovisionedInstanceIds { get; } = [];

    /// <summary>When set, <see cref="DeprovisionAsync"/> throws it.</summary>
    public Exception? DeprovisionFailure { get; set; }

    public int MaxConcurrentCalls { get; private set; }

    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>Makes the next <paramref name="count"/> calls fail.</summary>
    public void FailNextCalls(int count, Exception? exception = null)
    {
        lock (_lock)
        {
            _failuresRemaining = count;
            _failure = exception;
        }
    }

    public void FailAllCalls() => FailNextCalls(int.MaxValue);

    /// <summary>Holds every call at the gate until <see cref="Release"/> lets it through.</summary>
    public void Block() => _blocking = true;

    public void Release(int calls = 1) => _gate.Release(calls);

    /// <summary>Waits until <paramref name="count"/> more calls have started.</summary>
    public async Task WaitForCallsAsync(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            if (!await _started.WaitAsync(SignalTimeout))
            {
                throw new TimeoutException("The provisioner was not called in time.");
            }
        }
    }

    /// <summary>Returns whether another call starts within <paramref name="window"/>.</summary>
    public Task<bool> AnotherCallStartsWithinAsync(TimeSpan window) => _started.WaitAsync(window);

    public async Task ProvisionAsync(Instance instance, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            CallCount++;
            _inFlight++;
            MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, _inFlight);
        }

        _started.Release();

        try
        {
            if (_blocking)
            {
                await _gate.WaitAsync(cancellationToken);
            }

            lock (_lock)
            {
                if (_failuresRemaining > 0)
                {
                    _failuresRemaining--;
                    throw _failure ?? new InstanceProvisioningException("PROVISIONING_FAILED", "Simulated provisioning failure.");
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public Task DeprovisionAsync(Instance instance, CancellationToken cancellationToken)
    {
        if (DeprovisionFailure is not null)
        {
            throw DeprovisionFailure;
        }

        lock (_lock)
        {
            DeprovisionedInstanceIds.Add(instance.Id);
        }

        return Task.CompletedTask;
    }
}
