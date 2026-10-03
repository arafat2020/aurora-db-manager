# Scheduled backups

A database can have one backup schedule. When the schedule is due, Aurora creates an ordinary
backup of that database: the same `backup_database` job, the same dump, storage, checksum and
retries as a backup requested with `POST /api/v1/databases/{databaseId}/backups`. A schedule only
decides *when* that request is made.

## API

All four operations are on one resource, `/api/v1/databases/{databaseId}/backup-schedule`.

| Method | Result | Notes |
| --- | --- | --- |
| `POST` | `201` with the schedule | The database must be `ready` and its instance `running`. One schedule per database. |
| `GET` | `200` with the schedule | |
| `PUT` | `200` with the schedule | Replaces the cron expression, the time zone and `enabled`. |
| `DELETE` | `204` | Removes the schedule only. |

Request body of `POST` and `PUT`:

```json
{ "cronExpression": "0 2 * * *", "timeZoneId": "Asia/Dhaka", "enabled": true }
```

`enabled` is optional and means `true` when it is left out. Anything else in the body is ignored:
a client cannot set the next run, the storage, or the database.

Response:

```json
{
  "id": "0199…",
  "databaseId": "0198…",
  "cronExpression": "0 2 * * *",
  "timeZoneId": "Asia/Dhaka",
  "enabled": true,
  "nextRunAt": "2026-03-10T20:00:00Z",
  "createdAt": "2026-03-10T10:00:00Z",
  "updatedAt": "2026-03-10T10:00:00Z"
}
```

`nextRunAt` is always a UTC instant, and `null` while the schedule is disabled.

| Error code | Status | When |
| --- | --- | --- |
| `DATABASE_NOT_FOUND` | 404 | No such database. |
| `BACKUP_SCHEDULE_NOT_FOUND` | 404 | The database has no schedule (`GET`, `PUT`, `DELETE`). |
| `BACKUP_SCHEDULE_ALREADY_EXISTS` | 409 | `POST` for a database that has one. |
| `DATABASE_NOT_READY` | 409 | `POST` for a database that is not `ready`. |
| `INSTANCE_NOT_READY` | 409 | `POST` for a database whose instance is not `running`. |
| `INVALID_CRON_EXPRESSION` | 400 | See below. |
| `INVALID_TIME_ZONE` | 400 | See below. |

Creating, changing or deleting a schedule never creates a backup, never deletes one, and never
stops a backup that is running. Updating does not require the database to be backupable at that
moment. A schedule is deleted with its database (and so with its instance) and never stands in
the way of that deletion.

## Cron format

The standard five fields, read by the [Cronos](https://github.com/HangfireIO/Cronos) library:

```
┌ minute (0–59)
│ ┌ hour (0–23)
│ │ ┌ day of month (1–31)
│ │ │ ┌ month (1–12 or JAN–DEC)
│ │ │ │ ┌ day of week (0–7 or SUN–SAT; 0 and 7 are Sunday)
* * * * *
```

`*`, lists (`1,15`), ranges (`1-5`), steps (`*/6`) and Cronos' `L`, `W`, `#` and `?` are
accepted. Not accepted: a sixth (seconds) field, macros such as `@daily`, and expressions that
can never occur (`0 0 30 2 *`). The expression is stored with single spaces between fields.

## Time zones

`timeZoneId` is required and must be an IANA identifier (`UTC`, `Asia/Dhaka`,
`America/New_York`). Offsets (`+06:00`), Windows names and unknown identifiers are rejected.
Aurora never uses the server's local time zone and never substitutes UTC for a zone it does not
know.

The expression is evaluated in that zone and the result is stored as UTC. Daylight saving time
is handled by Cronos:

- A time that does not exist on the day clocks go forward (02:30 in New York in March) runs at
  the moment the gap ends instead of being lost.
- A time that occurs twice on the day clocks go back runs once, at its first occurrence.
  (Interval expressions such as `*/30 * * * *` do run in both hours.)

## How a schedule runs

`ScheduledBackupWorker` wakes every `Backups:Scheduler:PollIntervalSeconds` (default 30, between
1 and 3600; an invalid value stops the application at startup) and once at startup. A pass:

1. finds the enabled schedules whose `nextRunAt` is not in the future;
2. for each one, in a single database transaction: creates the backup and its `backup_database`
   job, and moves `nextRunAt` to the first occurrence after *now*;
3. hands the job to the job queue.

The worker does nothing else. It does not dump, upload, retry or track backups; the job system
does. It keeps no state in memory: the stored `nextRunAt` is the only record of what is owed, so
a backup can start up to one poll interval after its scheduled time.

**Exactly once per occurrence.** `nextRunAt` is the claim. It is a concurrency token: the
transaction that moves it forward only commits if it still has the value that was read. Two
passes, or two processes, that find the same occurrence due cannot both commit, and the backup
and job of the one that loses are never stored. No lock is held and none is needed.

**Missed occurrences.** If the application was down, or behind, for any number of occurrences,
one backup is created when it next looks, and `nextRunAt` goes straight to the first occurrence
in the future. Daily at 02:00 and down for a week means one backup, not seven.

**Overlap.** A database has at most one unfinished job (the existing rule that also keeps a
manual backup from running during a restore). If a schedule comes due while a backup, a restore,
or the deletion of its database is still unfinished, no backup is created for that occurrence
and the schedule still moves on. A backup that runs longer than the interval therefore causes no
queue of late backups. The same happens when the database is not `ready` or its instance is not
`running` at that moment: the occurrence is skipped (and logged), the instance is not started,
and the next occurrence is tried as usual.

**Restarts and crashes.** Before the transaction commits nothing has happened, and the occurrence
is still due after a restart. After it commits, the backup and its job exist and the schedule
has moved on; if the process stops before the job ran, the existing job recovery picks the job
up at startup. There is no point at which an occurrence can be both run and still due.

**Failures.** A scheduled backup that fails is a `failed` backup with a `failed` job, exactly
like a manual one. The schedule has no status of its own and carries on with its next occurrence.

## Storage

A scheduled backup goes to the configured default storage (`Backups:StorageType`), like a manual
one. A schedule has no storage setting.

## Not included

Retention and deletion of old backups, notifications, per-schedule storage, more than one
schedule per database, and a history of schedule runs (the backups and jobs are that history).
