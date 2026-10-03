using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Jobs;

namespace AuroraDbManager.Api.Tests.Backups;

public sealed class BackupTransitionTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private const string Checksum = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    private static Backup NewBackup() => Backup.Create(Guid.NewGuid(), BackupStorageType.Local, Now);

    private static Backup In(BackupStatus status)
    {
        var backup = NewBackup();
        if (status != BackupStatus.Pending)
        {
            backup.MarkRunning();
        }

        if (status == BackupStatus.Completed)
        {
            backup.MarkCompleted("/backups/a.dump", 10, BackupChecksumAlgorithm.Sha256, Checksum, Now);
        }

        if (status == BackupStatus.Failed)
        {
            backup.MarkFailed("CODE", "message", Now);
        }

        Assert.Equal(status, backup.Status);
        return backup;
    }

    [Fact]
    public void NewBackup_IsPending_WithNoArtifact()
    {
        var databaseId = Guid.NewGuid();

        var backup = Backup.Create(databaseId, BackupStorageType.Local, Now);

        Assert.NotEqual(Guid.Empty, backup.Id);
        Assert.Equal(databaseId, backup.DatabaseId);
        Assert.Equal(BackupStatus.Pending, backup.Status);
        Assert.Equal(BackupStorageType.Local, backup.StorageType);
        Assert.Equal(Now, backup.CreatedAt);
        Assert.Null(backup.Path);
        Assert.Null(backup.SizeBytes);
        Assert.Null(backup.ChecksumAlgorithm);
        Assert.Null(backup.Checksum);
        Assert.Null(backup.CompletedAt);
        Assert.Null(backup.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ba7816bf")]
    [InlineData("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")]
    [InlineData("ga7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad0")]
    [InlineData("sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f2")]
    public void MarkCompleted_WithoutAWellFormedChecksum_IsRejected_AndTheBackupStaysRunning(string checksum)
    {
        var backup = In(BackupStatus.Running);

        Assert.Throws<ArgumentException>(() => backup.MarkCompleted("/backups/a.dump", 10, BackupChecksumAlgorithm.Sha256, checksum, Now));

        Assert.Equal(BackupStatus.Running, backup.Status);
        Assert.Null(backup.Checksum);
        Assert.Null(backup.ChecksumAlgorithm);
    }

    [Fact]
    public void FailedBackup_HasNoChecksum()
    {
        var backup = In(BackupStatus.Failed);

        Assert.Null(backup.Checksum);
        Assert.Null(backup.ChecksumAlgorithm);
    }

    [Fact]
    public void Create_WithoutDatabase_Throws()
    {
        Assert.Throws<ArgumentException>(() => Backup.Create(Guid.Empty, BackupStorageType.Local, Now));
    }

    [Fact]
    public void PendingToRunning()
    {
        var backup = NewBackup();

        backup.MarkRunning();

        Assert.Equal(BackupStatus.Running, backup.Status);
        Assert.Null(backup.CompletedAt);
    }

    [Fact]
    public void RunningToCompleted_RecordsTheArtifact()
    {
        var backup = In(BackupStatus.Running);

        backup.MarkCompleted("/backups/a.dump", 1234, BackupChecksumAlgorithm.Sha256, Checksum, Now.AddMinutes(1));

        Assert.Equal(BackupStatus.Completed, backup.Status);
        Assert.Equal("/backups/a.dump", backup.Path);
        Assert.Equal(1234, backup.SizeBytes);
        Assert.Equal(BackupChecksumAlgorithm.Sha256, backup.ChecksumAlgorithm);
        Assert.Equal(Checksum, backup.Checksum);
        Assert.Equal(Now.AddMinutes(1), backup.CompletedAt);
        Assert.Null(backup.ErrorCode);
    }

    [Fact]
    public void RunningToFailed_RecordsTheError_AndNoArtifact()
    {
        var backup = In(BackupStatus.Running);

        backup.MarkFailed("BACKUP_PROCESS_FAILED", "The backup failed.", Now.AddMinutes(1));

        Assert.Equal(BackupStatus.Failed, backup.Status);
        Assert.Equal("BACKUP_PROCESS_FAILED", backup.ErrorCode);
        Assert.Equal("The backup failed.", backup.ErrorMessage);
        Assert.Equal(Now.AddMinutes(1), backup.CompletedAt);
        Assert.Null(backup.Path);
        Assert.Null(backup.SizeBytes);
    }

    [Fact]
    public void MarkFailed_TruncatesOverlongErrors()
    {
        var backup = In(BackupStatus.Running);

        backup.MarkFailed(new string('C', 100), new string('m', 2000), Now);

        Assert.Equal(Backup.ErrorCodeMaxLength, backup.ErrorCode!.Length);
        Assert.Equal(Backup.ErrorMessageMaxLength, backup.ErrorMessage!.Length);
    }

    [Theory]
    [InlineData(BackupStatus.Running)]
    [InlineData(BackupStatus.Completed)]
    [InlineData(BackupStatus.Failed)]
    public void MarkRunning_FromAnythingButPending_IsRejected(BackupStatus from)
    {
        var backup = In(from);

        Assert.Throws<InvalidOperationException>(backup.MarkRunning);
        Assert.Equal(from, backup.Status);
    }

    [Theory]
    [InlineData(BackupStatus.Pending)]
    [InlineData(BackupStatus.Completed)]
    [InlineData(BackupStatus.Failed)]
    public void MarkCompleted_FromAnythingButRunning_IsRejected(BackupStatus from)
    {
        var backup = In(from);

        Assert.Throws<InvalidOperationException>(() => backup.MarkCompleted("/backups/b.dump", 99, BackupChecksumAlgorithm.Sha256, Checksum, Now));
        Assert.Equal(from, backup.Status);
    }

    [Theory]
    [InlineData(BackupStatus.Pending)]
    [InlineData(BackupStatus.Completed)]
    [InlineData(BackupStatus.Failed)]
    public void MarkFailed_FromAnythingButRunning_IsRejected(BackupStatus from)
    {
        var backup = In(from);

        Assert.Throws<InvalidOperationException>(() => backup.MarkFailed("CODE", "message", Now));
        Assert.Equal(from, backup.Status);
    }

    [Theory]
    [InlineData("", 10)]
    [InlineData("/backups/a.dump", 0)]
    [InlineData("/backups/a.dump", -1)]
    public void MarkCompleted_WithoutARealArtifact_IsRejected(string path, long sizeBytes)
    {
        var backup = In(BackupStatus.Running);

        Assert.ThrowsAny<ArgumentException>(() => backup.MarkCompleted(path, sizeBytes, BackupChecksumAlgorithm.Sha256, Checksum, Now));
        Assert.Equal(BackupStatus.Running, backup.Status);
    }

    [Fact]
    public void Status_HasNoPublicSetter()
    {
        Assert.False(typeof(Backup).GetProperty(nameof(Backup.Status))!.SetMethod!.IsPublic);
    }

    [Fact]
    public void Job_BackupJob_CarriesItsDatabaseAndBackup_AndNeedsBoth()
    {
        var databaseId = Guid.NewGuid();
        var backupId = Guid.NewGuid();

        var job = Job.Create(JobType.BackupDatabase, Guid.NewGuid(), 3, Now, databaseId, backupId);

        Assert.Equal(databaseId, job.DatabaseId);
        Assert.Equal(backupId, job.BackupId);
        Assert.Throws<ArgumentException>(() => Job.Create(JobType.BackupDatabase, Guid.NewGuid(), 3, Now, databaseId));
        Assert.Throws<ArgumentException>(() => Job.Create(JobType.BackupDatabase, Guid.NewGuid(), 3, Now, backupId: backupId));
    }

    [Theory]
    [InlineData(JobType.CreateDatabase)]
    [InlineData(JobType.DeleteDatabase)]
    public void Job_OtherDatabaseJobs_CannotBeGivenABackup(JobType type)
    {
        Assert.Throws<ArgumentException>(() => Job.Create(type, Guid.NewGuid(), 3, Now, Guid.NewGuid(), Guid.NewGuid()));
        Assert.Null(Job.Create(type, Guid.NewGuid(), 3, Now, Guid.NewGuid()).BackupId);
    }
}
