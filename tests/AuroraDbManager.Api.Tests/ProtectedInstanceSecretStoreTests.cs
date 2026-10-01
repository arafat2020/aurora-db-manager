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
}
