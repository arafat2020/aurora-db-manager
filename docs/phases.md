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

