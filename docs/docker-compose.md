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
| `localstack` | A local stand-in for S3, with the bucket `aurora-backups`. Not published outside the compose network. |

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

## S3 storage

The S3 settings default to the `localstack` service, so this alone stores new backups in S3:

```bash
AURORA_BACKUP_STORAGE=s3
```

| Variable | Default |
| --- | --- |
| `AURORA_S3_BUCKET` | `aurora-backups` |
| `AURORA_S3_REGION` | `us-east-1` |
| `AURORA_S3_ENDPOINT` | `http://localstack:4566` |
| `AURORA_S3_ACCESS_KEY` | `test` |
| `AURORA_S3_SECRET_KEY` | `test` |
| `AURORA_S3_PATH_STYLE` | `true` |

To see what is in the bucket:

```bash
docker compose exec localstack awslocal s3 ls s3://aurora-backups --recursive
```

**LocalStack is for trying S3 backups out, not for keeping them.** It holds its objects in
memory: they are gone when the container restarts, while Aurora still lists the backups, which
then fail to restore. The bucket itself is created again on every start.

For AWS or another S3-compatible service, set all six variables in `.env`. A variable set to
nothing stays empty rather than falling back to its default, which is how the endpoint is left out
for AWS, and the keys for the role of the machine:

```bash
AURORA_BACKUP_STORAGE=s3
AURORA_S3_BUCKET=my-backups
AURORA_S3_REGION=eu-west-1
AURORA_S3_ENDPOINT=
AURORA_S3_ACCESS_KEY=
AURORA_S3_SECRET_KEY=
AURORA_S3_PATH_STYLE=false
```

The `localstack` container still runs then, unused. `AURORA_LOCALSTACK_IMAGE` chooses another
image for it.

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
