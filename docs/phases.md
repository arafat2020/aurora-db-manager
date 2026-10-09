# Self-Hosted DBaaS (Dotnet + Docker) — Development Roadmap

## 🎯 Goal

Build a self-managed database service that allows users to:

* Create and manage database instances (Postgres/MySQL)
* Create databases inside instances
* Backup & restore (local + S3)
* Run entirely on a VPS

---

# 🧱 Phase 0 — Foundation (Project Setup)

## Objectives

* Setup base .NET project
* Define architecture boundaries
* Prepare system database

## Tasks

* [ ] Create ASP.NET Core Web API project
* [ ] Setup PostgreSQL for system metadata
* [ ] Setup basic folder structure:

  * `/Controllers`
  * `/Services`
  * `/Infrastructure`
  * `/Domain`
* [ ] Add Docker support (for your app itself)
* [ ] Setup EF Core + migrations

## Output

* Running API
* Connected system database

---

# 🚀 Phase 1 — Instances API (Core Resource)

## Objectives

* Create and manage DB instances (logical only, no Docker yet)

## Tasks

* [ ] Design Instance entity
* [ ] Implement endpoints:

  * `POST /instances`
  * `GET /instances`
  * `GET /instances/{id}`
  * `DELETE /instances/{id}`
* [ ] Add instance status field:

  * provisioning
  * running
  * stopped
* [ ] Store metadata:

  * engine (postgres/mysql)
  * version
  * resource config

## Output

* Fully working Instances API (mocked execution)

---

# ⚙️ Phase 2 — Background Job System

## Objectives

* Handle long-running tasks asynchronously

## Tasks

* [ ] Integrate Hangfire (or build lightweight queue)
* [ ] Create Job model:

  * id
  * type
  * status
  * progress
* [ ] Implement job processing service
* [ ] Modify instance creation:

  * API → creates job
  * Job → handles provisioning logic

## Endpoints

* `GET /jobs/{id}`

## Output

* Async job system working
* Instance creation handled via jobs

---

# 🐳 Phase 3 — Docker Integration (Execution Layer)

## Objectives

* Actually create real database instances using Docker

## Tasks

* [ ] Integrate Docker SDK (`Docker.DotNet`)
* [ ] Create container service:

  * Create container
  * Start/stop/remove container
* [ ] Map instance → container
* [ ] Setup:

  * volumes (data persistence)
  * environment variables (DB config)
* [ ] Update job logic:

  * provisioning job creates container

## Output

* Real DB containers running from API

---

# 🗄️ Phase 4 — Database Management (Inside Instance)

## Objectives

* Manage databases inside running instances

## Tasks

* [ ] Create Database entity
* [ ] Implement:

  * `POST /instances/{id}/databases`
  * `GET /instances/{id}/databases`
* [ ] Connect to DB instance from .NET
* [ ] Execute SQL:

  * CREATE DATABASE
  * CREATE USER
  * GRANT PRIVILEGES

## Output

* Ability to create real databases inside containers

---

# 💾 Phase 5 — Backup System (Local First)

## Objectives

* Enable database backups

## Tasks

* [ ] Implement backup service
* [ ] Use:

  * `pg_dump` (Postgres)
  * `mysqldump` (MySQL later)
* [ ] Store backups on disk
* [ ] Create Backup entity

## Endpoints

* `POST /instances/{id}/backups`
* `GET /instances/{id}/backups`

## Output

* Manual backup system working

---

# ☁️ Phase 6 — S3 Integration

## Objectives

* Store backups externally

## Tasks

* [ ] Integrate S3-compatible storage
* [ ] Upload backup files
* [ ] Store metadata (URL, size, timestamp)

## Output

* Backups stored remotely

---

# 🔄 Phase 7 — Restore System

## Objectives

* Restore backups into instances

## Tasks

* [ ] Download backup
* [ ] Restore using:

  * `pg_restore` or `psql`
* [ ] Add restore job

## Endpoint

* `POST /backups/{id}/restore`

## Output

* Full backup → restore workflow

---

# ⏱️ Phase 8 — Scheduled Backups

## Objectives

* Automate backups

## Tasks

* [ ] Add backup policies
* [ ] Cron-based scheduling (Hangfire)
* [ ] Implement retention logic

## Output

* Automatic backups running daily

---

# 📊 Phase 9 — Monitoring & Metrics

## Objectives

* Visibility into system health

## Tasks

* [ ] Collect container stats (CPU, memory)
* [ ] Store metrics
* [ ] Expose:

  * `GET /instances/{id}/metrics`
* [ ] Add logs endpoint

## Output

* Basic observability

---

# 🔐 Phase 10 — Security & Multi-Tenancy

## Objectives

* Make system safe and usable by multiple users

## Tasks

* [ ] Add authentication (JWT)
* [ ] Add Projects (tenant boundary)
* [ ] Encrypt DB credentials
* [ ] Restrict access per user/project

## Output

* Multi-user system

---

# 🧠 Phase 11 — Hardening (Real-world readiness)

## Objectives

* Make system reliable

## Tasks

* [ ] Handle container crashes
* [ ] Retry failed jobs
* [ ] Disk space monitoring
* [ ] Resource limits per instance

## Output

* Production-ready base

---

# 🔌 Phase 13.1 — Database Connectivity & Controlled Port Exposure

## Objectives

* Let a client outside Docker connect to an instance, when an administrator says so
* Keep every instance private until then

## Tasks

* [x] Persist external access on the instance (`external_access_enabled`, `external_port`)
* [x] Allocate host ports from a configured range, one per instance, safely under concurrency
* [x] Publish the engine's port in Docker (5432 PostgreSQL, 3306 MySQL) on the configured bind address
* [x] Enable and disable external access by replacing the container on the same data volume
* [x] Verify the container's published port during provisioning and after a restart
* [x] Connection information in the API and the UI, without passwords
* [x] Administrators only; confirmation page in the UI

## Output

* An instance's database reachable on `<bind address>:<host port>` once enabled, private otherwise

## Not included

* TLS for database connections, proxies, tunnels, firewall automation
* Showing or rotating the instance's administrator password, and per-database users

See [security.md](security.md#external-database-access) and [ui.md](ui.md#connection).

---

# 💾 Phase 14 — Backup & Restore UI

## Objectives

* Make the existing backup and restore capabilities usable from the UI

## Tasks

* [x] A database's backups, paged, under `/instances/{id}/databases/{databaseId}/backups`
* [x] Create a backup; the destination is the server's configured storage, shown and not chosen
* [x] Backup details: status, size, storage, integrity, checksum, operations
* [x] Restore from a dedicated confirmation page, POST only
* [x] Storage shown per backup, also when it is no longer the server's default
* [x] Operators and administrators create and restore; viewers look

## Output

* Backups made, inspected and restored from the UI, through the existing services and job system

## Not included

* Deleting, downloading or uploading backups; restoring into another database
* Schedules and a cross-database overview

See [ui.md](ui.md#backups-and-restores).

---

# ⏱️ Phase 15 — Scheduling & Monitoring UI

## Objectives

* Make backup schedules, the job history and monitoring usable from the UI

## Tasks

* [x] A database's backup schedule under `/instances/{id}/databases/{databaseId}/schedule`: view, create, edit, enable/disable, delete
* [x] Cron and time zone validated by the schedule service; next run shown as the service calculated it
* [x] `/jobs` with the service's filters and paging, and `/jobs/{id}`
* [x] `/monitoring`: health checks, scheduler, activity, recent failures
* [x] Instance health on the database page
* [x] Operators and administrators change schedules; everyone signed in may look

## Output

* Schedules managed, jobs inspected and the installation's state read from the UI, through the existing services

## Not included

* A "last scheduled backup": nothing on record marks a backup as scheduled
* Health of a single database, a health status of the scheduler, charts, live updates
* Starting, retrying or cancelling jobs

See [ui.md](ui.md#backup-schedules).

---

# ✨ Phase 16 — Production UI Polish

## Objectives

* Make the existing UI consistent, responsive and accessible, without adding features

## Tasks

* [x] Audit of every page at 1280, 768 and 390 px, in light and dark
* [x] Tables shown a row at a time on narrow screens, with nothing left out; no sideways scrolling at desktop width
* [x] Refused form fields marked invalid and tied to their messages
* [x] Text contrast of at least 4.5:1 in both themes
* [x] One vocabulary across the pages: instance, database server, this host, Aurora
* [x] Tests of structure, form semantics, wording and role visibility across every page

## Output

* The same application, finished: no new routes, no backend, API, schema or deployment changes

## Noted for later, not built

* Per-database users and a way to hand out an instance's password
* Retrying and cancelling jobs; deleting and downloading backups
* Health of a single database and a health status for the scheduler
* Names instead of ids where the jobs list is filtered by instance or database
* Right-aligned numeric columns and sortable tables
* Live updates for long-running work

See [ui.md](ui.md).

---

# 🔑 Phase 17 — Database Credential & Password Management

## Objectives

* Rotate the password of an instance's database administrator, safely, without ever showing it

## Tasks

* [x] `rotate_credential` job in the existing job pipeline, for PostgreSQL and MySQL
* [x] `POST /api/v1/instances/{id}/credentials/rotate` (operator, admin) and `GET /api/v1/instances/{id}/credentials` (anyone signed in)
* [x] New password generated by Aurora, stored encrypted before the server is changed, promoted only after the server is seen to accept it
* [x] Recovery after a failed step, a retry or a restart, from which of the two stored passwords the server accepts
* [x] One rotation per instance at a time; refused while other work is going on in the instance, and the reverse
* [x] The new password handed out once: `POST /api/v1/instances/{id}/credentials/rotate/{jobId}/result` and *Show the new password* on the job page, to any operator or admin, for 15 minutes, consumed atomically
* [x] Credential section on the instance page, a confirmation page, and the job page saying how it went
* [x] Tests against real PostgreSQL 15, 16, 17 and MySQL 8.0, 8.4 containers, including failures in the middle

## Output

* The administrator password of an instance can be replaced on demand; the old one stops working, and the new one is disclosed exactly once, so an external client can be given it
* One migration: the new job type, a nullable column for the replacement while a rotation is under way, and three nullable columns recording whose result the stored password is, until when it may be given out, and when it was

## Not included

* Showing the password a second time, showing the instance's first password, or choosing a password
* Per-database users, roles and grants
* A SQL console

See [ui.md](ui.md#the-managed-credential) and [security.md](security.md#rotating-an-instances-password).

---

# 🚫 What NOT to Build (Yet)

Avoid these early:

* Kubernetes
* Multi-node orchestration
* Fancy UI dashboards
* Auto-scaling

---

# 🧭 Recommended Order (Strict)

1. Instances API
2. Job System
3. Docker Integration
4. Database Management
5. Backup System

---

# 🧩 Future Expansion Ideas

* Multi-node support
* UI dashboard
* Billing system
* Connection pooling
* Read replicas

---

# 📝 Notes

* Start with PostgreSQL only
* Keep everything simple first
* Ship working features, not perfect architecture

---

**End of roadmap**

