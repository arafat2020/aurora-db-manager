using AuroraDbManager.Api.Application.Instances;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuroraDbManager.Api.Tests;

public sealed class ProtectedInstanceSecretStoreTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public ProtectedInstanceSecretStoreTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<string> GetOrCreatePasswordAsync(Guid instanceId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IInstanceSecretStore>()
            .GetOrCreateAdminPasswordAsync(instanceId, default);
    }

    [Fact]
    public async Task GetOrCreate_ReturnsTheSamePasswordEveryTime()
    {
        var (instanceId, _) = await _client.CreateInstanceAsync();

        var first = await GetOrCreatePasswordAsync(instanceId);
        var second = await GetOrCreatePasswordAsync(instanceId);

        Assert.Equal(32, first.Length);
        Assert.All(first, character => Assert.True(char.IsAsciiLetterOrDigit(character)));
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task GetOrCreate_DifferentInstances_GetDifferentPasswords()
    {
        var (firstId, _) = await _client.CreateInstanceAsync(name: "first");
        var (secondId, _) = await _client.CreateInstanceAsync(name: "second");

        Assert.NotEqual(await GetOrCreatePasswordAsync(firstId), await GetOrCreatePasswordAsync(secondId));
    }

    [Fact]
    public async Task Password_IsStoredEncrypted()
    {
        var (instanceId, _) = await _client.CreateInstanceAsync();
        var password = await GetOrCreatePasswordAsync(instanceId);

        var stored = await _factory.WithDbAsync(db => db.InstanceSecrets.AsNoTracking().SingleAsync());

        Assert.Equal(instanceId, stored.InstanceId);
        Assert.DoesNotContain(password, stored.ProtectedAdminPassword);
    }

    [Fact]
    public async Task Password_IsNotExposedByInstanceOrJobResponses()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();
        var password = await GetOrCreatePasswordAsync(instanceId);
        await _factory.ProcessJobAsync(jobId);

        foreach (var url in new[]
                 {
                     $"{ApiClientExtensions.InstancesUrl}/{instanceId}", ApiClientExtensions.InstancesUrl,
                     $"{ApiClientExtensions.JobsUrl}/{jobId}"
                 })
        {
            var body = await _client.GetStringAsync(url);
            Assert.DoesNotContain(password, body);
            Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task DeletingTheInstance_RemovesItsSecret()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();
        await GetOrCreatePasswordAsync(instanceId);
        await _factory.ProcessJobAsync(jobId);

        await _client.DeleteAsync($"{ApiClientExtensions.InstancesUrl}/{instanceId}");

        Assert.Equal(0, await _factory.WithDbAsync(db => db.InstanceSecrets.CountAsync()));
    }

    // --- The replacement a rotation stores ------------------------------------------------------

    private async Task<T> WithStoreAsync<T>(Func<IInstanceSecretStore, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IInstanceSecretStore>());
    }

    [Fact]
    public async Task Replacement_IsGeneratedLikeAPassword_StoredEncrypted_AndLeavesThePasswordAlone()
    {
        var (instanceId, _) = await _client.CreateInstanceAsync();
        var password = await GetOrCreatePasswordAsync(instanceId);
        Assert.Equal(AdminCredentialState.Stored, await WithStoreAsync(store => store.GetAdminCredentialStateAsync(instanceId, default)));
        Assert.Null(await WithStoreAsync(store => store.GetAdminPasswordReplacementAsync(instanceId, default)));

        Assert.True(await WithStoreAsync(store => store.StageAdminPasswordReplacementAsync(instanceId, default)));

        var replacement = await WithStoreAsync(store => store.GetAdminPasswordReplacementAsync(instanceId, default));
        Assert.NotNull(replacement);
        Assert.Equal(32, replacement.Length);
        Assert.All(replacement, character => Assert.True(char.IsAsciiLetterOrDigit(character)));
        Assert.False(replacement == password, "The replacement is the password it replaces.");
        Assert.True(await GetOrCreatePasswordAsync(instanceId) == password);
        Assert.Equal(AdminCredentialState.ReplacementStaged, await WithStoreAsync(store => store.GetAdminCredentialStateAsync(instanceId, default)));

        var stored = await _factory.WithDbAsync(db => db.InstanceSecrets.AsNoTracking().SingleAsync());
        Assert.False(stored.ProtectedPendingAdminPassword!.Contains(replacement, StringComparison.Ordinal), "The replacement is stored in the clear.");
        Assert.False(stored.ProtectedAdminPassword.Contains(password, StringComparison.Ordinal), "The password is stored in the clear.");
    }

    [Fact]
    public async Task Replacement_OnceStored_IsNeverOverwritten()
    {
        var (instanceId, _) = await _client.CreateInstanceAsync();
        await GetOrCreatePasswordAsync(instanceId);
        await WithStoreAsync(store => store.StageAdminPasswordReplacementAsync(instanceId, default));
        var first = await WithStoreAsync(store => store.GetAdminPasswordReplacementAsync(instanceId, default));

        // Asked again, and asked by many at once.
        var again = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(
            () => WithStoreAsync(store => store.StageAdminPasswordReplacementAsync(instanceId, default)))));

        Assert.All(again, Assert.True);
        Assert.True(await WithStoreAsync(store => store.GetAdminPasswordReplacementAsync(instanceId, default)) == first);
    }

    [Fact]
    public async Task Replacement_Promoted_BecomesThePassword_AndPromotingAgainChangesNothing()
    {
        var (instanceId, _) = await _client.CreateInstanceAsync();
        var password = await GetOrCreatePasswordAsync(instanceId);
        await WithStoreAsync(store => store.StageAdminPasswordReplacementAsync(instanceId, default));
        var replacement = await WithStoreAsync(store => store.GetAdminPasswordReplacementAsync(instanceId, default));

        await WithStoreAsync(async store =>
        {
            await store.PromoteAdminPasswordReplacementAsync(instanceId, default);
            await store.PromoteAdminPasswordReplacementAsync(instanceId, default);
            return true;
        });

        var current = await GetOrCreatePasswordAsync(instanceId);
        Assert.True(current == replacement);
        Assert.False(current == password);
        Assert.Null(await WithStoreAsync(store => store.GetAdminPasswordReplacementAsync(instanceId, default)));
        Assert.Equal(AdminCredentialState.Stored, await WithStoreAsync(store => store.GetAdminCredentialStateAsync(instanceId, default)));
        Assert.Equal(1, await _factory.WithDbAsync(db => db.InstanceSecrets.CountAsync()));
    }

    [Fact]
    public async Task Replacement_ForAnInstanceWithoutAPassword_IsNotStored()
    {
        var unknown = Guid.NewGuid();

        Assert.False(await WithStoreAsync(store => store.StageAdminPasswordReplacementAsync(unknown, default)));
        Assert.Equal(AdminCredentialState.None, await WithStoreAsync(store => store.GetAdminCredentialStateAsync(unknown, default)));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.InstanceSecrets.CountAsync()));
    }

    [Fact]
    public async Task GeneratedPasswords_AreAllDifferent_AndCarryNothingOfTheirInstance()
    {
        var passwords = new List<string>();
        for (var index = 0; index < 12; index++)
        {
            var (instanceId, _) = await _client.CreateInstanceAsync(name: $"instance-{index}");
            passwords.Add(await GetOrCreatePasswordAsync(instanceId));
            await WithStoreAsync(store => store.StageAdminPasswordReplacementAsync(instanceId, default));
            passwords.Add((await WithStoreAsync(store => store.GetAdminPasswordReplacementAsync(instanceId, default)))!);

            var compact = instanceId.ToString("N");
            Assert.All(passwords[^2..], generated => Assert.False(
                generated.Contains(compact[..8], StringComparison.OrdinalIgnoreCase) || generated.Contains("instance", StringComparison.OrdinalIgnoreCase),
                "A password was derived from its instance."));
        }

        Assert.Equal(passwords.Count, passwords.Distinct().Count());
        // Drawn from all of the alphabet, not from a corner of it: letters of both cases and digits turn up.
        var all = string.Concat(passwords);
        Assert.True(all.Any(char.IsAsciiLetterUpper) && all.Any(char.IsAsciiLetterLower) && all.Any(char.IsAsciiDigit));
        Assert.True(all.Distinct().Count() > 50);
    }
}
