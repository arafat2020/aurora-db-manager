using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Tests.Databases;

/// <summary>
/// The engine side of every instance, in memory: which databases exist in which instance's
/// server. Its managers behave like the real ones, creating an existing database and deleting a
/// missing one both succeed, and their outcome and timing are scripted by the test.
/// </summary>
public sealed class FakeDatabaseServers
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private readonly HashSet<(Guid InstanceId, string Name)> _databases = [];
    private readonly SemaphoreSlim _started = new(0);
    private readonly SemaphoreSlim _gate = new(0);
    private volatile bool _blocking;
    private int _failuresRemaining;
    private Exception? _failure;

    /// <summary>Every manager call in order, as "engine operation name outcome".</summary>
    public List<string> Calls { get; } = [];

    public int CallCount
    {
        get
        {
            lock (_lock)
            {
                return Calls.Count;
            }
        }
    }

    public bool Exists(Guid instanceId, string name)
    {
        lock (_lock)
        {
            return _databases.Contains((instanceId, name));
        }
    }

    /// <summary>Puts a database into an instance's server, as an attempt that was interrupted afterwards would have.</summary>
    public void Add(Guid instanceId, string name)
    {
        lock (_lock)
        {
            _databases.Add((instanceId, name));
        }
    }

    /// <summary>Makes the next <paramref name="count"/> calls fail.</summary>
    public void FailNextCalls(int count, Exception? exception = null)
    {
        lock (_lock)
        {
            _failuresRemaining = count;
            _failure = exception;
        }
    }

    public void FailAllCalls(Exception? exception = null) => FailNextCalls(int.MaxValue, exception);

    /// <summary>Holds every call at the gate until <see cref="Release"/> lets it through.</summary>
    public void Block() => _blocking = true;

    public void Release(int calls = 1) => _gate.Release(calls);

    /// <summary>Waits until one more call has started.</summary>
    public async Task WaitForCallAsync()
    {
        if (!await _started.WaitAsync(SignalTimeout))
        {
            throw new TimeoutException("The database manager was not called in time.");
        }
    }

    public IDatabaseManager ManagerFor(InstanceEngine engine) => new Manager(engine, this);

    private async Task RunAsync(
        InstanceEngine engine, string operation, Instance instance, Database database, CancellationToken cancellationToken)
    {
        if (instance.Engine != engine)
        {
            throw new InvalidOperationException($"The {engine} manager was given a {instance.Engine} instance.");
        }

        _started.Release();
        if (_blocking)
        {
            await _gate.WaitAsync(cancellationToken);
        }

        lock (_lock)
        {
            var prefix = $"{engine.ToString().ToLowerInvariant()} {operation} {database.Name}";

            if (_failuresRemaining > 0)
            {
                _failuresRemaining--;
                Calls.Add($"{prefix} failed");
                throw _failure ?? new DatabaseOperationException(
                    DatabaseErrorCodes.DatabaseConnectionFailed,
                    "Could not connect to the instance's database server.",
                    new IOException("raw-driver-detail password=hunter2"));
            }

            var key = (instance.Id, database.Name);
            var changed = operation == "create" ? _databases.Add(key) : _databases.Remove(key);
            Calls.Add($"{prefix} {(changed ? "done" : operation == "create" ? "already-exists" : "already-absent")}");
        }
    }

    private sealed class Manager(InstanceEngine engine, FakeDatabaseServers servers) : IDatabaseManager
    {
        public InstanceEngine Engine => engine;

        public Task CreateDatabaseAsync(Instance instance, Database database, CancellationToken cancellationToken) =>
            servers.RunAsync(engine, "create", instance, database, cancellationToken);

        public Task DeleteDatabaseAsync(Instance instance, Database database, CancellationToken cancellationToken) =>
            servers.RunAsync(engine, "delete", instance, database, cancellationToken);
    }
}
