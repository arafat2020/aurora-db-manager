using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Jobs;

namespace AuroraDbManager.Api.Tests.Databases;

public sealed class DatabaseTransitionTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Database NewDatabase() => Database.Create(Guid.NewGuid(), "app", Now);

    private static Database In(DatabaseStatus status)
    {
        var database = NewDatabase();
        if (status is DatabaseStatus.Ready or DatabaseStatus.Deleting)
        {
            database.MarkReady(Now);
        }

        if (status == DatabaseStatus.Deleting)
        {
            database.MarkDeleting(Now);
        }

        if (status == DatabaseStatus.Failed)
        {
            database.MarkFailed("CODE", "message", Now);
        }

        Assert.Equal(status, database.Status);
        return database;
    }

    [Fact]
    public void NewDatabase_IsCreatingWithNoError()
    {
        var database = NewDatabase();

        Assert.Equal(DatabaseStatus.Creating, database.Status);
        Assert.Null(database.ErrorCode);
        Assert.Null(database.ErrorMessage);
    }

    [Fact]
    public void CreatingThenReadyThenDeleting_FollowsLifecycle()
    {
        var database = NewDatabase();

        database.MarkReady(Now.AddSeconds(1));
        Assert.Equal(DatabaseStatus.Ready, database.Status);
        Assert.Equal(Now.AddSeconds(1), database.UpdatedAt);

        database.MarkDeleting(Now.AddSeconds(2));
        Assert.Equal(DatabaseStatus.Deleting, database.Status);
        Assert.Equal(Now.AddSeconds(2), database.UpdatedAt);
        Assert.Equal(Now, database.CreatedAt);
    }

    [Theory]
    [InlineData(DatabaseStatus.Creating)]
    [InlineData(DatabaseStatus.Deleting)]
    public void MarkFailed_FromCreatingOrDeleting_RecordsTheError(DatabaseStatus from)
    {
        var database = In(from);

        database.MarkFailed("DATABASE_CONNECTION_FAILED", "Could not connect.", Now.AddSeconds(5));

        Assert.Equal(DatabaseStatus.Failed, database.Status);
        Assert.Equal("DATABASE_CONNECTION_FAILED", database.ErrorCode);
        Assert.Equal("Could not connect.", database.ErrorMessage);
        Assert.Equal(Now.AddSeconds(5), database.UpdatedAt);
    }

    [Fact]
    public void MarkFailed_TruncatesOverlongErrors()
    {
        var database = NewDatabase();

        database.MarkFailed(new string('C', 100), new string('m', 2000), Now);

        Assert.Equal(Database.ErrorCodeMaxLength, database.ErrorCode!.Length);
        Assert.Equal(Database.ErrorMessageMaxLength, database.ErrorMessage!.Length);
    }

    [Theory]
    [InlineData(DatabaseStatus.Ready)]
    [InlineData(DatabaseStatus.Deleting)]
    [InlineData(DatabaseStatus.Failed)]
    public void MarkReady_FromAnythingButCreating_IsRejected(DatabaseStatus from)
    {
        var database = In(from);

        Assert.Throws<InvalidOperationException>(() => database.MarkReady(Now));
        Assert.Equal(from, database.Status);
    }

    [Theory]
    [InlineData(DatabaseStatus.Creating)]
    [InlineData(DatabaseStatus.Deleting)]
    [InlineData(DatabaseStatus.Failed)]
    public void MarkDeleting_FromAnythingButReady_IsRejected(DatabaseStatus from)
    {
        var database = In(from);

        Assert.Throws<InvalidOperationException>(() => database.MarkDeleting(Now));
        Assert.Equal(from, database.Status);
    }

    [Theory]
    [InlineData(DatabaseStatus.Ready)]
    [InlineData(DatabaseStatus.Failed)]
    public void MarkFailed_FromReadyOrFailed_IsRejected(DatabaseStatus from)
    {
        var database = In(from);

        Assert.Throws<InvalidOperationException>(() => database.MarkFailed("CODE", "message", Now));
        Assert.Equal(from, database.Status);
    }

    [Fact]
    public void Status_HasNoPublicSetter()
    {
        Assert.False(typeof(Database).GetProperty(nameof(Database.Status))!.SetMethod!.IsPublic);
    }

    [Theory]
    [InlineData(JobType.CreateDatabase)]
    [InlineData(JobType.DeleteDatabase)]
    public void Job_DatabaseJob_CarriesItsDatabase_AndCannotBeCreatedWithoutOne(JobType type)
    {
        var databaseId = Guid.NewGuid();

        var job = Job.Create(type, Guid.NewGuid(), 3, Now, databaseId);

        Assert.Equal(databaseId, job.DatabaseId);
        Assert.Throws<ArgumentException>(() => Job.Create(type, Guid.NewGuid(), 3, Now));
    }

    [Fact]
    public void Job_ProvisioningJob_HasNoDatabase_AndCannotBeGivenOne()
    {
        var job = Job.Create(JobType.ProvisionInstance, Guid.NewGuid(), 3, Now);

        Assert.Null(job.DatabaseId);
        Assert.Throws<ArgumentException>(() => Job.Create(JobType.ProvisionInstance, Guid.NewGuid(), 3, Now, Guid.NewGuid()));
    }
}
