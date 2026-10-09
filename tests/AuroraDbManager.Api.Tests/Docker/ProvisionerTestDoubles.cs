using AuroraDbManager.Api.Application.Instances;
using Microsoft.Extensions.Logging;

namespace AuroraDbManager.Api.Tests.Docker;

public sealed class InMemoryInstanceSecretStore : IInstanceSecretStore
{
    private readonly Dictionary<Guid, string> _passwords = [];

    public int CreatedCount => _passwords.Count;

    public Task<string> GetOrCreateAdminPasswordAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        if (!_passwords.TryGetValue(instanceId, out var password))
        {
            password = $"pw-{Guid.NewGuid():N}";
            _passwords[instanceId] = password;
        }

        return Task.FromResult(password);
    }

    private readonly Dictionary<Guid, string> _replacements = [];

    public Task<AdminCredentialState> GetAdminCredentialStateAsync(Guid instanceId, CancellationToken cancellationToken) =>
        Task.FromResult(
            !_passwords.ContainsKey(instanceId) ? AdminCredentialState.None
            : _replacements.ContainsKey(instanceId) ? AdminCredentialState.ReplacementStaged
            : AdminCredentialState.Stored);

    public Task<bool> StageAdminPasswordReplacementAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        if (!_passwords.ContainsKey(instanceId))
        {
            return Task.FromResult(false);
        }

        if (_replacements.TryAdd(instanceId, $"pw-{Guid.NewGuid():N}"))
        {
            _deliveries.Remove(instanceId);
        }

        return Task.FromResult(true);
    }

    public Task<string?> GetAdminPasswordReplacementAsync(Guid instanceId, CancellationToken cancellationToken) =>
        Task.FromResult(_replacements.GetValueOrDefault(instanceId));

    public Task PromoteAdminPasswordReplacementAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        if (_replacements.Remove(instanceId, out var replacement))
        {
            _passwords[instanceId] = replacement;
        }

        return Task.CompletedTask;
    }

    private readonly Dictionary<Guid, AdminPasswordDelivery> _deliveries = [];

    public Task OfferAdminPasswordAsync(Guid instanceId, Guid jobId, DateTime expiresAt, CancellationToken cancellationToken)
    {
        if (!_deliveries.TryGetValue(instanceId, out var delivery) || delivery.JobId != jobId)
        {
            _deliveries[instanceId] = new AdminPasswordDelivery(jobId, expiresAt, null);
        }

        return Task.CompletedTask;
    }

    public Task<AdminPasswordDelivery?> GetAdminPasswordDeliveryAsync(Guid instanceId, CancellationToken cancellationToken) =>
        Task.FromResult(_deliveries.GetValueOrDefault(instanceId));

    public Task<string?> ClaimAdminPasswordAsync(Guid instanceId, Guid jobId, DateTime utcNow, CancellationToken cancellationToken)
    {
        if (!_deliveries.TryGetValue(instanceId, out var delivery) || delivery.JobId != jobId || delivery.ConsumedAt is not null || delivery.ExpiresAt <= utcNow)
        {
            return Task.FromResult<string?>(null);
        }

        _deliveries[instanceId] = delivery with { ConsumedAt = utcNow };
        return Task.FromResult<string?>(_passwords[instanceId]);
    }
}

/// <summary>A clock that moves forward by <see cref="Step"/> every time it is read.</summary>
public sealed class SteppingTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public TimeSpan Step { get; set; } = TimeSpan.FromSeconds(1);

    public override DateTimeOffset GetUtcNow()
    {
        var now = _now;
        _now += Step;
        return now;
    }
}

/// <summary>Collects everything logged, including exception text, for assertions.</summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    public List<string> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}
