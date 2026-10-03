using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Domain.Instances;
using Microsoft.Extensions.Logging;

namespace AuroraDbManager.Api.Tests.Backups;

/// <summary>Resolves every instance to one fixed, made-up address, or fails the way an unreachable instance does.</summary>
public sealed class FakeInstanceEndpoints : IInstanceEndpointResolver
{
    public const string Host = "10.20.30.40";

    /// <summary>When set, resolving throws it.</summary>
    public DatabaseOperationException? Failure { get; set; }

    public Task<InstanceEndpoint> ResolveAsync(Instance instance, CancellationToken cancellationToken) =>
        Failure is null
            ? Task.FromResult(new InstanceEndpoint(Host, instance.Engine == InstanceEngine.Postgres ? 5432 : 3306))
            : Task.FromException<InstanceEndpoint>(Failure);
}

/// <summary>Collects everything the application logs, from every category, including exception text.</summary>
public sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly Lock _lock = new();
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToList();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(RecordingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            lock (provider._lock)
            {
                provider._entries.Add($"{category} scope: {state}");
            }

            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (provider._lock)
            {
                provider._entries.Add($"{category} {logLevel}: {formatter(state, exception)} {exception}");
            }
        }
    }
}
