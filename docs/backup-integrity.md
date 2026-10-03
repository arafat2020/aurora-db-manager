# Backup integrity

Every backup completed by Aurora carries a checksum of its artifact, and a restore checks the
artifact against it before it changes anything in the target database.

## What is recorded

| Field | Value |
| --- | --- |
| `checksumAlgorithm` | `sha256` |
| `checksum` | SHA-256 of the artifact, 64 lowercase hexadecimal characters |

Both are returned with a backup by the API. The checksum is calculated from the exact bytes of
the artifact file (`pg_dump` custom-format archive, or `mysqldump` SQL script), read as a stream;
it is not derived from the database's rows, from metadata, or from the storage's own bookkeeping.

## When a backup is made

1. The dump is written and validated.
2. Its SHA-256 is calculated.
3. The artifact is stored: moved into place for local storage, uploaded for S3.
4. The stored artifact is read back from the storage and hashed again. Its size and its checksum
   must be those of the file that was written.
5. Only then is the checksum recorded and the backup marked `completed`.

A backup whose stored artifact does not match fails with `BACKUP_CHECKSUM_MISMATCH` and is never
`completed`; the job's ordinary retries apply. For S3 the object's ETag is **not** used as a
checksum: it is not a SHA-256, and for uploads sent in parts it is not a hash of the content at
all. The object is read back and hashed.

An S3 object also states its checksum in the metadata `aurora-checksum-sha256`. That is only used
when a retry finds an object left by an interrupted attempt: the object is adopted if its bytes,
read back, have the checksum it states. Once a backup is completed, the checksum in Aurora's own
record is the one that counts.

## When a backup is restored

1. The artifact is fetched into the restore's own staging directory.
2. Its size must be the recorded size.
3. Its SHA-256 must be the recorded checksum.
4. It must look like a dump of the engine.
5. Only then is the target database emptied and the artifact loaded.

A mismatch fails the restore with `RESTORE_ARTIFACT_CHECKSUM_MISMATCH` before the database has
been connected to. Neither the expected nor the actual checksum is returned to the client; both
are written to the server's log.

## Backups from before checksums

Backups completed before checksums were introduced have no `checksum` and no `checksumAlgorithm`
(both `null`). They stay valid and restorable. Nothing is invented for them: a restore of such a
backup checks size and format only, as it did before, and does not calculate or store a checksum.
A corruption that keeps the size and the format intact is therefore not detected for these
backups; take a new backup to get one that is protected.
