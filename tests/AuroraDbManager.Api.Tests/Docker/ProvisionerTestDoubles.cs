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
