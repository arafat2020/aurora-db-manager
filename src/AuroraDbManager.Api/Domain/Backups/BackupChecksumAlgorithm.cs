namespace AuroraDbManager.Api.Domain.Backups;

/// <summary>How the checksum of a backup's artifact was calculated.</summary>
public enum BackupChecksumAlgorithm
{
    /// <summary>SHA-256 over the artifact's bytes, written as 64 lowercase hexadecimal characters.</summary>
    Sha256
}
