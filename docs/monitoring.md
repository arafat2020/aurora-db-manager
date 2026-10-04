# Monitoring and observability

Aurora reports on itself in three ways: **health** endpoints, **metrics**, and **logs**. All
three only observe the services that do the work.

```text
   Health          Metrics          Logs
     │                │               │
     └────────────────┼───────────────┘
                      ▼
        existing application services
```

Nothing in monitoring runs a backup, a restore, a job, a schedule or a Docker operation, nothing
is decided from a metric, and nothing is repaired or restarted because of what is observed. If
monitoring itself fails (a metrics listener throws, a summary query fails, the S3 probe times
out), the operation being observed is not affected.

There is no monitoring database and no bundled monitoring stack. Operational state is read from
where it already is: the `jobs`, `backups`, `instances` and `backup_schedules` tables.

## Liveness ≠ readiness ≠ operational health

These are three different questions with three different answers, and the endpoints are kept
apart on purpose.

| Question | Endpoint | Depends on |
| --- | --- | --- |
| Is the Aurora process alive? | `GET /health` (public) | Nothing outside the process |
| Can Aurora do its management work now? | `GET /health/ready` (public) | System database, Docker |
| What exactly is wrong? | `GET /health/storage`, `GET /api/v1/instances/{id}/health`, `GET /api/v1/monitoring/summary`, `GET /api/v1/jobs` | What each one names |

Use `/health` for a liveness probe and `/health/ready` for a readiness probe. Do not point a
liveness probe at `/health/ready`: a database or Docker outage would then restart a process that
is working correctly.

## Health endpoints

All three return the same shape, and nothing else: no descriptions, exception text, host names,
paths or connection details. Why a check failed is in the server's log.

```json
{
  "status": "healthy",
  "checks": {
    "metadataDatabase": "healthy",
    "docker": "healthy"
  }
}
```

`status` is the worst status among the checks: `healthy`, `degraded` or `unhealthy`. The response
is `200` for `healthy` and `degraded`, and `503` for `unhealthy`.

### `GET /health` — liveness

Runs no check. It returns `200` with `{"status":"healthy","checks":{}}` whenever the process can
answer a request, also while the system database, Docker or S3 are down.

### `GET /health/ready` — readiness

| Check | What it does | When it fails |
| --- | --- | --- |
| `metadataDatabase` | One `SELECT 1` on the system database. No migration, no table scan. | `unhealthy` → `503` |
| `docker` | One ping of the Docker Engine. No container is listed or inspected. | `degraded` → `200` |

The system database is required for everything, so without it Aurora is not ready. Docker is
needed to provision instances and to reach their databases, but reading state, following jobs and
all of monitoring work without it, and jobs that need Docker are retried. A Docker that cannot be
reached therefore makes Aurora `degraded` rather than unavailable; the `docker` check says which
dependency it is.

Each check gives up after five seconds. Health checks never change anything: Docker is not
restarted and no instance is touched.

### `GET /health/storage` — S3 backup storage

Needs an access token (any role), unlike the two probes above. Separate from readiness on purpose. Backups may be local, and an S3 outage fails backups, not
the API.

| Situation | `checks.s3` | Status |
| --- | --- | --- |
| No S3 settings on this server | `not_applicable` | `200` |
| Bucket reachable and usable | `healthy` | `200` |
| Store unreachable, credentials rejected, or bucket missing | `unhealthy` | `503` |

The check is one `HEAD` request on the configured bucket (the credentials need `s3:ListBucket`
for it). No object is listed, read or written. It is a request to the object store each time it
is called, so use it for diagnosis or an infrequent check, not as a probe every few seconds.

## Instance health

```http
GET /api/v1/instances/{instanceId}/health
```

Looks at one instance's database server at the time of the request: one inspection of its
container and, if it is running, the engine's own readiness command (`pg_isready`,
`mysqladmin ping`) run once inside it.

```json
{
  "instanceId": "0199…",
  "status": "healthy",
  "instanceStatus": "running",
  "container": { "exists": true, "running": true },
  "database": { "reachable": true },
  "checkedAt": "2026-03-10T10:00:00Z"
}
```

| `status` | `reason` | Meaning |
| --- | --- | --- |
| `healthy` | — | Container running, database accepting connections. |
| `degraded` | `database_not_ready` | Container running, database not answering (starting, recovering, overloaded). |
| `degraded` | `runtime_unavailable` | Docker could not be asked. Nothing is known about the instance, so `container` and `database` values are `null`; it is not reported as down. |
| `unhealthy` | `container_not_running` | The container exists but is stopped. |
| `unhealthy` | `container_missing` | The instance has no container of its own. |

The response is `200` whatever the health is, and `404 INSTANCE_NOT_FOUND` for an unknown
instance. Passwords, connection details, container names and environment are never returned.

**Metadata status and runtime health are different things.** `instanceStatus` is what Aurora has
on record; `status` is what was just observed. `instanceStatus: "running"` with
`status: "unhealthy"` is possible and is exactly what this endpoint is for. The health request
never changes the instance's status and never starts or repairs anything: state transitions
belong to provisioning and reconciliation.

## Job history

```http
GET /api/v1/jobs?status=failed&type=backup_database&instanceId=…&databaseId=…&page=1&pageSize=20
```

Every background operation is a job, so this is the history of everything Aurora did:
provisioning, database creation and deletion, backups and restores. All filters are optional and
combine. `status` is `pending`, `running`, `completed` or `failed`; `type` is `provision_instance`,
`create_database`, `delete_database`, `backup_database` or `restore_database`.

Results are newest first and always paged: `pageSize` defaults to 20 and is at most 100. A failed
job carries its stable error code in `error.code`. `GET /api/v1/jobs/{id}` is unchanged.

Jobs are not cleaned up automatically in this phase.

## Backups

The existing list, `GET /api/v1/databases/{databaseId}/backups`, now also takes `status`
(`pending`, `running`, `completed`, `failed`). Together with paging that answers the usual
operational questions in one request:

- latest backup: `?pageSize=1`
- latest good backup: `?status=completed&pageSize=1`
- what failed, and why: `?status=failed` — each item has `error.code`

Every backup has its status, storage type, size, checksum, `createdAt` and `completedAt`; the
time a backup took, waiting included, is the difference of the last two. The location of the
artifact (path, bucket, key) is never returned.

## Monitoring summary

```http
GET /api/v1/monitoring/summary
```

A snapshot counted from the system database.

```json
{
  "status": "degraded",
  "generatedAt": "2026-03-10T10:00:00Z",
  "recentWindowHours": 24,
  "instances": { "total": 3, "provisioning": 0, "running": 2, "stopped": 0, "failed": 1 },
  "jobs": { "pending": 2, "running": 1, "failedRecently": 1 },
  "backups": { "pending": 1, "running": 1, "failedRecently": 0 },
  "restores": { "pending": 0, "running": 0, "failedRecently": 0 },
  "scheduler": {
    "enabledSchedules": 4,
    "overdueSchedules": 0,
    "nextRunAt": "2026-03-10T20:00:00Z",
    "lastSuccessfulPassAt": "2026-03-10T09:59:40Z"
  },
  "recentFailures": [
    {
      "jobId": "0199…",
      "type": "provision_instance",
      "instanceId": "0198…",
      "errorCode": "DATABASE_READINESS_TIMEOUT",
      "failedAt": "2026-03-10T09:12:00Z"
    }
  ]
}
```

- `status` is `degraded` when there is something to look at: an instance in status `failed`, a
  job that failed within the recent window, or an overdue schedule. Otherwise it is `healthy`.
  It is never `unhealthy`; whether Aurora itself can work is what `/health/ready` answers.
- `instances` counts instances by their status on record. No instance is inspected: for that,
  use the instance health endpoint.
- `backups` and `restores` are the backup and restore jobs among `jobs`. Every backup and every
  restore is one job, and the job's outcome is theirs.
- `failedRecently` counts jobs that failed for good in the last 24 hours.
- `recentFailures` lists at most the ten most recent of those, newest first, with the stable
  error code only. The human-readable message stays on the job (`GET /api/v1/jobs/{id}`).

The endpoint makes a fixed, small number of bounded queries. It makes no Docker request and no S3
request, runs no health check, and keeps no cache.

## Scheduler observability

The backup scheduler is observable in three ways.

- **Heartbeat.** `scheduler.lastSuccessfulPassAt` in the summary is the time of the last pass
  that went through. It is kept in memory, so it is `null` after a restart until the first pass
  (which happens right at startup). It proves that this process's scheduler is alive; it is not
  written to the database.
- **Overdue schedules.** `scheduler.overdueSchedules` counts enabled schedules whose next run is
  more than two scheduler intervals in the past. This is read from the persisted `nextRunAt`,
  so it holds across restarts, and it is the signal to alert on: a scheduler that is not running,
  or a schedule it cannot deal with.
- **Metrics and logs**, below.

The persisted `nextRunAt` of each schedule stays the only source of truth for what is due.

## Metrics

Aurora emits standard .NET metrics (`System.Diagnostics.Metrics`) on one meter:

```text
AuroraDbManager
```

**No exporter, Prometheus or Grafana is bundled**, and none is needed for Aurora to run. To see
the metrics, attach anything that listens to .NET meters:

```bash
dotnet-counters monitor --process-id <pid> --counters AuroraDbManager
```

or add an OpenTelemetry or Prometheus exporter to the host and subscribe it to the
`AuroraDbManager` meter. The names below are the instrument names as Aurora defines them; an
exporter may add a unit suffix of its own.

### Jobs

| Instrument | Type | Tags | Meaning |
| --- | --- | --- | --- |
| `aurora_jobs_started_total` | counter | `job_type` | A job was claimed and work began. |
| `aurora_jobs_completed_total` | counter | `job_type` | A job completed. |
| `aurora_jobs_failed_total` | counter | `job_type`, `error_code` | A job failed for good, all attempts used. |
| `aurora_jobs_retried_total` | counter | `job_type` | An attempt failed and another follows. |
| `aurora_jobs_cancelled_total` | counter | `job_type` | An execution was given up before the job finished: shutdown, or a lost lease. The job itself is picked up again. |
| `aurora_job_duration` | histogram, s | `job_type`, `status` | From claiming the job to its completion or final failure, retries included. |
| `aurora_jobs_pending` | gauge | — | Jobs waiting, counted from the `jobs` table when the metrics are collected. |
| `aurora_jobs_running` | gauge | — | Jobs being worked on, counted the same way. |

The two gauges are read from the persisted jobs, not from the in-memory queue: the queue holds
only ids, is lost with the process and can hold an id twice, so its length is not a queue depth.

### Backups and restores

| Instrument | Type | Tags | Meaning |
| --- | --- | --- | --- |
| `aurora_backups_started_total` | counter | `engine`, `storage_type` | A backup's job began to work on it. Once per backup. |
| `aurora_backups_completed_total` | counter | `engine`, `storage_type` | A backup was stored and verified. |
| `aurora_backups_failed_total` | counter | `engine`, `storage_type`, `error_code` | A backup failed for good. Once per backup. |
| `aurora_backup_duration` | histogram, s | `engine`, `storage_type`, `status` | One attempt to dump, store and verify. A backup that needed retries has one measurement per attempt. |
| `aurora_backup_size_bytes` | histogram, bytes | `engine`, `storage_type` | Size of a completed backup. |
| `aurora_restores_started_total` | counter | `engine`, `storage_type` | A restore's job began to work on it. |
| `aurora_restores_completed_total` | counter | `engine`, `storage_type` | A restore completed. |
| `aurora_restores_failed_total` | counter | `engine`, `storage_type`, `error_code` | A restore failed for good. |
| `aurora_restore_duration` | histogram, s | `engine`, `storage_type`, `status` | One attempt to fetch, verify and load a backup. |

### Scheduler

| Instrument | Type | Tags | Meaning |
| --- | --- | --- | --- |
| `aurora_scheduler_runs_total` | counter | — | A scheduler pass was made. |
| `aurora_scheduler_failures_total` | counter | — | A pass failed, or a schedule in it could not be dealt with. |
| `aurora_scheduler_pass_duration` | histogram, s | — | How long a pass took. |
| `aurora_scheduled_backups_triggered_total` | counter | — | A schedule created a backup. |
| `aurora_scheduled_backups_skipped_total` | counter | `reason` | A due occurrence created no backup. |

Skip reasons: `backup_in_progress`, `restore_in_progress`, `database_not_ready`,
`instance_not_ready`, `database_not_found`, `database_busy` (another operation took the database
between the check and the save).

### Tags

Tags come from small, fixed sets only:

| Tag | Values |
| --- | --- |
| `job_type` | `provision_instance`, `create_database`, `delete_database`, `backup_database`, `restore_database` |
| `engine` | `postgres`, `mysql`, `unknown` |
| `storage_type` | `local`, `s3`, `unknown` |
| `status` | `completed`, `failed` |
| `error_code` | The stable error codes, e.g. `BACKUP_STORAGE_UNAVAILABLE`, `RESTORE_ARTIFACT_INVALID` |
| `reason` | The skip reasons above |

**Ids are never tags.** No job, backup, instance, database or schedule id, no container id, no
path, object key or error message is ever used as a tag: those grow without bound and would turn
every operation into a time series of its own. To follow one operation, use the logs or the job
history, where ids belong.

Durations are measured with a monotonic clock, not as a difference of wall-clock timestamps.

## Logs

Aurora logs through the standard ASP.NET Core logging. Messages are message templates with named
fields, so a structured sink gets `JobId`, `InstanceId`, `DatabaseId`, `BackupId`, `ScheduleId`,
`JobType`, `DurationMs`, `ErrorCode` and so on as properties, and the console gets a readable
line. An entry carries the fields that are relevant to it, not all of them.

### Request correlation

Every request has an id.

- A client may send `X-Request-Id`. It is used if it is at most 64 characters of letters,
  digits, `-`, `_` and `.`; anything else is ignored and never logged.
- Otherwise Aurora generates one.
- The id is returned in the `X-Request-Id` response header, on error responses too.
- Everything logged while the request is handled is in the scope `Request {CorrelationId}`, and
  each request is logged once it is done: method, path (never the query string), status code,
  duration and the id of the user who made it (`anonymous` if nobody was signed in; never the
  token). Health probes are logged at debug level only.

When a request creates a job, the worker processes that job under the same `CorrelationId`, and
everything a job logs is in the scope `Job {JobId} ({JobType})`:

```text
HTTP request → X-Request-Id → request logs → "Job {JobId} … queued" → worker and job logs
```

The request id is not stored on the job (there is no column for it, and none was added). It
travels with the in-memory queue, so a job that is picked up again after a restart, or created by
the scheduler, has no request id; from there on `JobId` is the key, and it is in every line.

No record is written per request.

### Scheduler events

| Message | Level | Fields |
| --- | --- | --- |
| `Scheduled backup scheduler pass completed` | information (debug when nothing was due) | `DueCount`, `TriggeredCount`, `SkippedCount`, `FailedCount`, `DurationMs` |
| `Scheduled backup triggered` | information | `ScheduleId`, `DatabaseId`, `JobId`, `BackupId`, `Occurrence`, `NextRunAt` |
| `Scheduled backup skipped` | warning | `ScheduleId`, `DatabaseId`, `Reason`, `Occurrence`, `NextRunAt` |
| `Scheduled backup scheduler pass failed` | error | `ErrorType`, and `ScheduleId` when one schedule failed rather than the pass |

### Failures

A job that fails for good is logged at error level with `JobId`, `JobType`, `InstanceId`,
`DurationMs` and its stable `ErrorCode`; a failed backup or restore additionally with its
`BackupId` and `DatabaseId`. The same code is on the job, on the backup, in the metrics and in
the monitoring summary, so `BACKUP_STORAGE_NOT_CONFIGURED` can be told from
`BACKUP_STORAGE_UNAVAILABLE` without reading text.

## Sensitive data

The rule that already applies to the rest of Aurora applies to monitoring without exception.
The following never appear in a log line, a metric, a health response or a monitoring response:

- database passwords and connection strings
- S3 access keys and secret keys
- the contents of `PGPASSFILE` and MySQL credential files
- environment secrets and authorization tokens
- backup contents

Monitoring responses additionally leave out what is internal rather than secret: exception text
and stack traces, the Docker endpoint, container names, file system paths, S3 endpoints, buckets
and object keys. Health responses contain statuses only; the summary contains counts, ids, types,
times and stable error codes only.

`/health` and `/health/ready` are public, so that whatever runs Aurora can probe it without a
user; that is why they are kept this conservative. Everything else in monitoring, `/health/storage`
included, needs a signed-in user: any role may read it. See [authentication.md](authentication.md).

## Configuration

Monitoring needs no configuration. The log levels are the standard `Logging` section; the
console formatter includes scopes, which is where the request and job ids are shown.

## What this phase does not include

No alerts, notifications or webhooks, no Prometheus/Grafana/OpenTelemetry collector, no
distributed tracing, no audit log, no container resource statistics (CPU, memory), no automatic
restart or repair, and no cleanup of old jobs.
