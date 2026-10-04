# Running Aurora with Docker Compose

The quickest way to run a complete Aurora on one machine: the UI, the REST API, the background
workers and Aurora's own metadata database.

## Start

```bash
cp .env.example .env
```

Set three values in `.env`:

```bash
AURORA_JWT_SIGNING_KEY=        # openssl rand -base64 48
AURORA_SYSTEM_DB_PASSWORD=     # openssl rand -hex 24
AURORA_ADMIN_PASSWORD=         # the first administrator's password, 12+ characters
```

Then:

```bash
docker compose up -d --build
```

Open <http://localhost:8080> and sign in as `admin` (or whatever `AURORA_ADMIN_USERNAME` says)
with the password you chose. The REST API is on the same address under `/api/v1`.

The first build takes a while: it downloads the .NET images and the database client programs,
and compiles the migration bundle. Later builds reuse what has not changed.

After the first start, remove `AURORA_ADMIN_USERNAME` and `AURORA_ADMIN_PASSWORD` from `.env`:
they are only used while no administrator exists. `.env` is not committed; keep it private.

## What runs

| Service | What it is |
| --- | --- |
| `system-db` | PostgreSQL holding Aurora's metadata. Not published outside the compose network. |
| `migrate` | Applies the schema migrations, then exits. Runs on every `up`; a no-op when up to date. |
| `aurora` | The application: UI, API, job worker and scheduler. |

The database instances you create in Aurora are **not** compose services. Aurora creates them
itself, as containers named `aurora-instance-<id>` on the `aurora-instances` network, through the
Docker socket that is mounted into the `aurora` container. `docker compose down` does not stop or
remove them.

| Volume | Holds | If lost |
| --- | --- | --- |
| `aurora_system-db-data` | Aurora's metadata | Aurora forgets everything it manages. |
| `aurora_backups` | Local backups | The backups are gone. |
| `aurora_keys` | Data Protection keys | Stored instance passwords can no longer be decrypted, and everyone is signed out. |

Back all three up together.

## Everyday commands

```bash
docker compose logs -f aurora          # the application log
docker compose ps                      # what is running
docker compose up -d --build           # after pulling a new version: rebuild, migrate, restart
docker compose down                    # stop Aurora; volumes and instances are kept
docker compose down -v                 # stop and DELETE metadata, backups and keys
```

## Settings

Everything in `.env` is described in `.env.example`. The ones most often changed:

| Variable | Default | |
| --- | --- | --- |
| `AURORA_PORT` | `8080` | The port on this machine. |
| `AURORA_BIND` | `127.0.0.1` | Aurora listens on this machine only. |
| `AURORA_HTTPS` | `false` | Set to `true` behind a TLS-terminating reverse proxy. |
| `AURORA_BACKUP_STORAGE` | `local` | `s3` to store new backups in the configured bucket. |

Any other setting of Aurora can be given to the `aurora` service as an environment variable, with
`__` between the parts of its name: `Jobs__MaxConcurrency`, `Web__SessionHours`, and so on.

## Before exposing it to a network

Out of the box this is a local installation: plain HTTP, reachable from this machine only. To
serve other machines:

1. Put a reverse proxy that terminates TLS in front of it, and keep `AURORA_BIND=127.0.0.1` (or
   put the proxy on the compose network and publish no port at all).
2. Set `AURORA_HTTPS=true`. Sign-in cookies are then marked `Secure` and only work over HTTPS.
3. Add the proxy to `Security__TrustedProxies__0` in `docker-compose.yml`, as the address or
   network the `aurora` container sees it under.

See [security.md](security.md) for the rest, in particular what mounting the Docker socket means:
the `aurora` container has root-equivalent control of the Docker host. That is what lets it manage
database containers, and it is why the container runs as root and should be treated as part of
the host's trusted software.
