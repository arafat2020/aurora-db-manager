using AuroraDbManager.Api.Application.Credentials;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Tests.Credentials;

/// <summary>
/// The administrator password of every instance's database server, in memory: what each server
/// accepts, and the means for a test to make changing it fail, pretend, or never answer. A server
/// starts out accepting what the secret store holds for its instance, as a provisioned one does.
/// </summary>
public sealed class FakeAdminCredentials
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, string> _passwords = [];
    private readonly SemaphoreSlim _started = new(0);
    private readonly SemaphoreSlim _gate = new(0);
    private volatile bool _blocking;
    private int _changeFailuresRemaining;
    private int _ignoredChangesRemaining;
    private int _changes;

    /// <summary>Reads the password an instance was provisioned with; set by the host.</summary>
    public Func<Guid, Task<string>> ProvisionedPassword { get; set; } = _ => throw new InvalidOperationException("No password source.");

    /// <summary>When set, the server cannot be reached at all: every call fails with this.</summary>
    public DatabaseOperationException? Unreachable { get; set; }

    /// <summary>How many times a server actually changed its password.</summary>
    public int Changes => Volatile.Read(ref _changes);

    /// <summary>Every call in order, as "engine operation outcome". Never a password.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Makes the next <paramref name="count"/> password changes fail in the server, changing nothing.</summary>
    public void FailNextChanges(int count) => Volatile.Write(ref _changeFailuresRemaining, count);

    /// <summary>Makes the next <paramref name="count"/> password changes report success and change nothing.</summary>
    public void IgnoreNextChanges(int count) => Volatile.Write(ref _ignoredChangesRemaining, count);

    /// <summary>Holds every password change until <see cref="Release"/> lets it through.</summary>
    public void Block() => _blocking = true;

    public void Release(int calls = 1) => _gate.Release(calls);

    public async Task WaitForChangeAsync()
    {
        if (!await _started.WaitAsync(SignalTimeout))
        {
            throw new TimeoutException("No password change was started in time.");
        }
    }

    /// <summary>What the instance's server accepts right now.</summary>
    public async Task<string> PasswordAsync(Guid instanceId)
    {
        lock (_lock)
        {
            if (_passwords.TryGetValue(instanceId, out var known))
            {
                return known;
            }
        }

        var provisioned = await ProvisionedPassword(instanceId);
        lock (_lock)
        {
            _passwords.TryAdd(instanceId, provisioned);
            return _passwords[instanceId];
        }
    }

    /// <summary>Changes the server's password behind Aurora's back, or as an attempt that died afterwards would have.</summary>
    public void SetPassword(Guid instanceId, string password)
    {
        lock (_lock)
        {
            _passwords[instanceId] = password;
        }
    }

    public IAdminCredentialManager ManagerFor(InstanceEngine engine) => new Manager(engine, this);

    private void Record(InstanceEngine engine, string what)
    {
        lock (_lock)
        {
            Calls.Add($"{engine.ToString().ToLowerInvariant()} {what}");
        }
    }

    private sealed class Manager(InstanceEngine engine, FakeAdminCredentials servers) : IAdminCredentialManager
    {
        public InstanceEngine Engine => engine;

        public async Task<bool> AuthenticatesAsync(Instance instance, string password, CancellationToken cancellationToken)
        {
            Check(instance);
            var accepted = await servers.PasswordAsync(instance.Id) == password;
            servers.Record(engine, accepted ? "authenticate accepted" : "authenticate refused");
            return accepted;
        }

        public async Task ChangeAdminPasswordAsync(
            Instance instance, string currentPassword, string newPassword, CancellationToken cancellationToken)
        {
            Check(instance);
            if (await servers.PasswordAsync(instance.Id) != currentPassword)
            {
                servers.Record(engine, "change refused");
                throw new DatabaseOperationException(
                    DatabaseErrorCodes.DatabaseConnectionFailed, "Could not connect to the instance's database server.");
            }

            servers._started.Release();
            if (servers._blocking)
            {
                await servers._gate.WaitAsync(cancellationToken);
            }

            if (Interlocked.Decrement(ref servers._changeFailuresRemaining) >= 0)
            {
                servers.Record(engine, "change failed");
                throw new DatabaseOperationException(
                    CredentialErrorCodes.DatabaseFailed, "The database server did not change the administrator password.");
            }

            Volatile.Write(ref servers._changeFailuresRemaining, 0);

            if (Interlocked.Decrement(ref servers._ignoredChangesRemaining) >= 0)
            {
                servers.Record(engine, "change ignored");
                return;
            }

            Volatile.Write(ref servers._ignoredChangesRemaining, 0);

            servers.SetPassword(instance.Id, newPassword);
            Interlocked.Increment(ref servers._changes);
            servers.Record(engine, "change done");
        }

        private void Check(Instance instance)
        {
            if (instance.Engine != engine)
            {
                throw new InvalidOperationException($"The {engine} manager was given a {instance.Engine} instance.");
            }

            if (servers.Unreachable is { } unreachable)
            {
                servers.Record(engine, "unreachable");
                throw unreachable;
            }
        }
    }
}

/// <summary>What a test makes the secret store do wrong. One per host, shared by every scope's store.</summary>
public sealed class SecretStoreFaults
{
    private int _promotionFailuresRemaining;
    private int _promotions;

    /// <summary>How many times a replacement was made the stored password.</summary>
    public int Promotions => Volatile.Read(ref _promotions);

    /// <summary>Makes the next <paramref name="count"/> attempts to store the replacement as the password fail, storing nothing.</summary>
    public void FailNextPromotions(int count) => Volatile.Write(ref _promotionFailuresRemaining, count);

    private int _claimFailuresRemaining;
    private int _offerFailuresRemaining;

    /// <summary>
    /// Makes the next <paramref name="count"/> attempts to hand a password out fail after the store
    /// has recorded handing it out and read it: the worst moment, with the caller's transaction still open.
    /// </summary>
    public void FailNextClaimsAfterClaiming(int count) => Volatile.Write(ref _claimFailuresRemaining, count);

    /// <summary>Makes the next <paramref name="count"/> attempts to put a rotated password on offer fail.</summary>
    public void FailNextOffers(int count) => Volatile.Write(ref _offerFailuresRemaining, count);

    internal void AfterClaim() => FailIfAsked(ref _claimFailuresRemaining);

    internal void BeforeOffer() => FailIfAsked(ref _offerFailuresRemaining);

    private static void FailIfAsked(ref int remaining)
    {
        if (Interlocked.Decrement(ref remaining) >= 0)
        {
            throw new IOException("raw-store-detail: the secret could not be read");
        }

        Volatile.Write(ref remaining, 0);
    }

    internal void BeforePromotion()
    {
        if (Interlocked.Decrement(ref _promotionFailuresRemaining) >= 0)
        {
            throw new IOException("raw-store-detail: the secret could not be written");
        }

        Volatile.Write(ref _promotionFailuresRemaining, 0);
        Interlocked.Increment(ref _promotions);
    }
}

/// <summary>The real secret store, with the means for a test to make it fail at the step that matters most.</summary>
public sealed class FaultInjectingSecretStore(IInstanceSecretStore real, SecretStoreFaults faults) : IInstanceSecretStore
{
    public Task<string> GetOrCreateAdminPasswordAsync(Guid instanceId, CancellationToken cancellationToken) =>
        real.GetOrCreateAdminPasswordAsync(instanceId, cancellationToken);

    public Task<AdminCredentialState> GetAdminCredentialStateAsync(Guid instanceId, CancellationToken cancellationToken) =>
        real.GetAdminCredentialStateAsync(instanceId, cancellationToken);

    public Task<bool> StageAdminPasswordReplacementAsync(Guid instanceId, CancellationToken cancellationToken) =>
        real.StageAdminPasswordReplacementAsync(instanceId, cancellationToken);

    public Task<string?> GetAdminPasswordReplacementAsync(Guid instanceId, CancellationToken cancellationToken) =>
        real.GetAdminPasswordReplacementAsync(instanceId, cancellationToken);

    public Task PromoteAdminPasswordReplacementAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        faults.BeforePromotion();
        return real.PromoteAdminPasswordReplacementAsync(instanceId, cancellationToken);
    }

    public Task OfferAdminPasswordAsync(Guid instanceId, Guid jobId, DateTime expiresAt, CancellationToken cancellationToken)
    {
        faults.BeforeOffer();
        return real.OfferAdminPasswordAsync(instanceId, jobId, expiresAt, cancellationToken);
    }

    public Task<AdminPasswordDelivery?> GetAdminPasswordDeliveryAsync(Guid instanceId, CancellationToken cancellationToken) =>
        real.GetAdminPasswordDeliveryAsync(instanceId, cancellationToken);

    public async Task<string?> ClaimAdminPasswordAsync(Guid instanceId, Guid jobId, DateTime utcNow, CancellationToken cancellationToken)
    {
        var password = await real.ClaimAdminPasswordAsync(instanceId, jobId, utcNow, cancellationToken);
        if (password is not null)
        {
            faults.AfterClaim();
        }

        return password;
    }
}
