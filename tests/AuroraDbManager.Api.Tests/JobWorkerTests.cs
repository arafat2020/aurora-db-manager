namespace AuroraDbManager.Api.Tests;

/// <summary>
/// End-to-end tests with the real background worker. Its completion is observed by polling the
/// job endpoint until the job is finished.
/// </summary>
public sealed class JobWorkerTests
{
    [Fact]
    public async Task Worker_ProcessesQueuedJob_AndInstanceBecomesRunning()
    {
        using var factory = new ApiFactory { RunWorker = true };
        using var client = factory.CreateClient();

        var (instanceId, jobId) = await client.CreateInstanceAsync();
        var job = await client.WaitForFinishedJobAsync(jobId);

        Assert.Equal("completed", job.Status());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        Assert.Equal("running", (await client.GetInstanceAsync(instanceId)).Status());
        Assert.Equal(1, factory.Provisioner.CallCount);
    }

    [Fact]
    public async Task Worker_ProvisioningKeepsFailing_JobAndInstanceBecomeFailed()
    {
        using var factory = new ApiFactory { RunWorker = true };
        using var client = factory.CreateClient();
        factory.Provisioner.FailAllCalls();

        var (instanceId, jobId) = await client.CreateInstanceAsync();
        var job = await client.WaitForFinishedJobAsync(jobId);

        Assert.Equal("failed", job.Status());
        Assert.Equal(3, job.GetProperty("attempt").GetInt32());
        Assert.Equal("failed", (await client.GetInstanceAsync(instanceId)).Status());
        Assert.Equal(3, factory.Provisioner.CallCount);
    }

    [Fact]
    public async Task Worker_OneJobFails_KeepsProcessingOtherJobs()
    {
        using var factory = new ApiFactory { RunWorker = true, MaxConcurrency = 1 };
        using var client = factory.CreateClient();
        factory.Provisioner.FailNextCalls(3);

        var (_, failingJobId) = await client.CreateInstanceAsync(name: "first");
        var (_, nextJobId) = await client.CreateInstanceAsync(name: "second");

        Assert.Equal("failed", (await client.WaitForFinishedJobAsync(failingJobId)).Status());
        Assert.Equal("completed", (await client.WaitForFinishedJobAsync(nextJobId)).Status());
    }

    [Fact]
    public async Task Worker_RespectsConcurrencyLimit()
    {
        const int limit = 2;
        const int jobCount = 5;
        using var factory = new ApiFactory { RunWorker = true, MaxConcurrency = limit };
        using var client = factory.CreateClient();
        var provisioner = factory.Provisioner;
        provisioner.Block();

        var jobIds = new List<Guid>();
        for (var i = 0; i < jobCount; i++)
        {
            jobIds.Add((await client.CreateInstanceAsync(name: $"db-{i}")).JobId);
        }

        // The worker fills its slots and every further job has to wait for one to free up.
        await provisioner.WaitForCallsAsync(limit);
        Assert.Equal(limit, provisioner.InFlight);

        // Absence can only be observed over a window. A correct worker can never fail this,
        // however slow the machine; the window only decides how reliably a broken limit is caught.
        Assert.False(
            await provisioner.AnotherCallStartsWithinAsync(TimeSpan.FromSeconds(1)),
            "A job started although every worker slot was busy.");

        // Let one job through at a time; each release frees exactly one slot for the next job.
        for (var started = limit; started < jobCount; started++)
        {
            provisioner.Release();
            await provisioner.WaitForCallsAsync();
        }

        provisioner.Release(limit);
        foreach (var jobId in jobIds)
        {
            Assert.Equal("completed", (await client.WaitForFinishedJobAsync(jobId)).Status());
        }

        Assert.Equal(jobCount, provisioner.CallCount);
        Assert.Equal(limit, provisioner.MaxConcurrentCalls);
    }
}
