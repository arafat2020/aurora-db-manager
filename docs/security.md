# Security

How Aurora is meant to be deployed and what it does to protect itself. Signing in, roles and
users are in [authentication.md](authentication.md); what is logged and measured is in
[monitoring.md](monitoring.md).

Aurora is a management service for one self-hosted installation: a REST API for tools, and a
server-rendered UI for operators ([ui.md](ui.md)). It serves nothing to browsers of other sites.

## Trust boundaries

```text
client ──HTTPS──► reverse proxy ──► Aurora API ──► Docker daemon ──► managed database containers
                                        │
                                        ├──► system database (PostgreSQL)
                                        └──► backup storage (local directory, S3 bucket)
```

- **The Docker daemon is a highly privileged dependency.** Whoever can talk to it controls the
  host. Aurora needs that access to do its job, so the machine and the account Aurora runs under
  must be treated accordingly, and the Docker socket must not be reachable by anything else that
  is less trusted. Aurora exposes none of Docker through its API: a client chooses an engine, a
  version from a fixed catalog and resource sizes, and nothing else.
- **The system database** holds users (password hashes), instance administrator passwords
  (encrypted at rest with ASP.NET Core Data Protection) and all metadata. Access to it is access
  to everything.
- **Backup storage** holds full copies of the databases. Protect it like the databases themselves.

## Deployment checklist

1. Serve Aurora over **HTTPS only**, with TLS terminated by a reverse proxy.
2. List that proxy in `Security:TrustedProxies`.
3. Set `Authentication:Jwt:SigningKey` from a secret store. Create the first administrator, then
   remove the bootstrap settings ([authentication.md](authentication.md)).
4. Run with `ASPNETCORE_ENVIRONMENT=Production` (the default when it is not set).
5. Set `AllowedHosts` to the host name(s) Aurora is served under.
6. Make sure Aurora is reachable only through the proxy, and the Docker socket only by Aurora.
7. Give the S3 credentials only the permissions listed below.
8. Leave `ExternalAccess:BindAddress` at `127.0.0.1` unless database clients on other machines
   have to connect; if they do, put a firewall in front of the port range first
   ([External database access](#external-database-access)).

## Configuration

```text
Security:
  TrustedProxies: [ "10.0.0.5", "172.16.0.0/12" ]
  ForwardLimit: 1
  HttpsRedirection: true
  MaxRequestBodyBytes: 1048576
  LoginRateLimit:
    PermitLimit: 10
    WindowSeconds: 60
```

| Setting | Default | Notes |
| --- | --- | --- |
| `Security:TrustedProxies` | empty | Addresses or CIDR networks of the reverse proxies. Empty: no forwarded header is read. |
| `Security:ForwardLimit` | `1` | How many proxies in a row are in front of Aurora. 1 to 10. |
| `Security:HttpsRedirection` | `true` | Redirect HTTP to HTTPS, and send HSTS outside development. |
| `Security:MaxRequestBodyBytes` | `1048576` | Largest request body. 1 KiB to 100 MiB. |
| `Security:LoginRateLimit:PermitLimit` | `10` | Sign-in attempts per client address per window. |
| `Security:LoginRateLimit:WindowSeconds` | `60` | Length of the window. |

Invalid values stop Aurora at startup with a message that names the setting.

## HTTPS

Production traffic must be HTTPS: an access token sent over plain HTTP can be read and reused by
anyone on the path. Aurora does not manage certificates; terminate TLS at a reverse proxy (Caddy,
Nginx, a cloud load balancer) and forward to Aurora over a private network or loopback.

- With `Security:HttpsRedirection` on (the default), a plain-HTTP request is redirected to HTTPS
  when Aurora knows its HTTPS port, and outside the development environment HTTPS responses carry
  `Strict-Transport-Security`, so clients keep using HTTPS.
- Aurora knows a request was HTTPS either because it terminated TLS itself, or because a
  **trusted** proxy said so in `X-Forwarded-Proto`.
- A redirect does not protect a token that was already sent over HTTP. Configure clients with
  the `https://` URL, and have the proxy refuse or redirect HTTP itself.
- In development nothing is pinned to HTTPS, so local work and the test suites run over HTTP.
- Turn `Security:HttpsRedirection` off only where there is deliberately no TLS at all, such as
  an isolated test network.

## Reverse proxy and forwarded headers

Behind a proxy, every connection Aurora sees comes from the proxy. The client's address and the
original scheme arrive in `X-Forwarded-For` and `X-Forwarded-Proto`, which any client can also
send itself. Aurora therefore believes them only from the proxies you name:

```text
Security:TrustedProxies: [ "10.0.0.5" ]          # one proxy
Security:TrustedProxies: [ "172.18.0.0/16" ]     # a Docker network the proxy is on
```

- **Empty (the default): the headers are ignored altogether.** The client address is the address
  of the connection. This is correct when clients reach Aurora directly, and safe, though coarse,
  when a proxy is in front but not configured: all clients then share the proxy's address, and
  with it one login rate limit.
- **Configured:** a request from a listed address has its `X-Forwarded-For` and
  `X-Forwarded-Proto` applied; a request from anywhere else has them ignored. Only the last
  `ForwardLimit` addresses, the ones your proxies appended, are used: what a client puts in
  front of them is not.
- Not even loopback is trusted unless listed. If the proxy runs on the same machine, list
  `127.0.0.1` (and `::1`).
- `0.0.0.0/0`, `::/0` and the like are refused: "everyone" is not a proxy.
- **`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` is refused at startup.** That switch makes
  ASP.NET Core trust forwarded headers from every client, which would let any client pick its own
  address and walk past the login rate limit.

The proxy must set (not merely pass along) the two headers. Caddy does so by default. For Nginx:

```nginx
location / {
    proxy_pass http://127.0.0.1:5177;
    proxy_set_header Host              $host;
    proxy_set_header X-Forwarded-For   $remote_addr;
    proxy_set_header X-Forwarded-Proto $scheme;
}
```

Behind a CDN such as Cloudflare in front of your own proxy, there are two proxies in a row: list
your own proxy, have it trust the CDN's addresses and rewrite `X-Forwarded-For` to the real
client, and leave `ForwardLimit` at 1; or list both and set `ForwardLimit` to 2.

## Login rate limiting

`POST /api/v1/auth/login` accepts 10 attempts per client address per minute by default
(ASP.NET Core's built-in rate limiter, a fixed window, kept in memory).

- Every attempt counts, successful or not. A client that signs in normally makes one request per
  token lifetime and never notices.
- Beyond the limit the answer is `429 TOO_MANY_REQUESTS` with `Retry-After` in seconds, for right
  and wrong credentials alike, so the limit cannot be used to tell them apart. Nothing else about
  the limiter is disclosed, and each refusal is logged with the client address.
- The limit is per address, not per username, so guessing one password across many accounts is
  limited just the same. An IPv6 client is counted as its /64 network.
- Only the login endpoint is limited. Management requests are not counted, and are not affected
  when a client is refused at login.
- The client address is the connection's, or the one a trusted proxy reports. A forwarded header
  from anyone else does not change it.

The limiter is per process and forgets on restart. There is no account lockout, deliberately: it
would let anyone lock an administrator out. Running several Aurora processes behind one balancer
multiplies the effective limit; put a shared limit in the proxy if that matters.

## Response headers

Every response, errors included, carries:

| Header | Value | Why |
| --- | --- | --- |
| `X-Content-Type-Options` | `nosniff` | A response is what its content type says. |
| `Content-Security-Policy` | `default-src 'none'; frame-ancestors 'none'` | Nothing Aurora returns is a page; if a browser renders one anyway, it loads and runs nothing. |
| `X-Frame-Options` | `DENY` | The same, for browsers that predate `frame-ancestors`. |
| `Referrer-Policy` | `no-referrer` | URLs contain resource ids. |
| `Cache-Control` | `no-store` | Responses are for the authorized caller, not for a shared cache. |
| `Strict-Transport-Security` | on HTTPS, outside development | See HTTPS. |

Aurora does not send `Server` or `X-Powered-By`.

The policy above is the API's. Pages of the UI get one that allows this host's own styles and
scripts and nothing else, still without `unsafe-inline` or `unsafe-eval`; see [ui.md](ui.md).

## CORS

**CORS is not enabled, intentionally.** Aurora has no browser front end on another origin, so no
origin is allowed, no `Access-Control-*` header is ever sent, and preflight requests fail. A
browser on another site cannot read Aurora's responses. If a browser UI on another origin is
ever added, allow exactly that origin; never `*`.

API tokens are sent in the `Authorization` header and never in a cookie, and the API does not
accept the UI's session cookie, so the API has no ambient credential for a cross-site request to
ride on. The UI's own forms are protected by antiforgery tokens.

## Request limits

A request body may be at most `Security:MaxRequestBodyBytes` (1 MiB by default). A larger one is
refused with `413 REQUEST_TOO_LARGE` before it is read; a body of undeclared length is cut off by
the server at the same size. Every body in this API is a small JSON document, and backups never
travel through the API, so the default is generous.

Other inputs are bounded too: page sizes (at most 100) and page numbers (at most 1,000,000),
string lengths, instance resources (256 CPUs, 1 TiB of memory, 65,536 GB of storage), cron
expressions (five fields, 100 characters) and time zones (IANA names). Ids are GUIDs; anything
else matches no route. Role, status, type and engine values are fixed sets.

## Process execution

Aurora runs `pg_dump`, `pg_restore`, `mysqldump` and `mysql` on its own machine, and
`pg_isready` / `mysqladmin ping` inside instance containers.

- Programs are started directly, never through a shell, with each argument passed separately.
  There is no command string, so there is nothing for `;`, `&&`, `$(…)`, backticks or redirection
  to act on.
- The only user-chosen value that reaches a program is a database name, and a database name is
  lowercase letters, digits and underscores, starting with a letter. Instance names are display
  names and are never passed to anything.
- Passwords are never arguments. They are handed over in a private temporary file (mode `0600`)
  named by an option or environment variable, removed afterwards.
- The program paths come from configuration (`Backups:Tools`), not from requests.
- Commands run inside containers are the two fixed readiness commands.

## Docker

- Container, volume and network names and labels are derived from the instance id and the
  configuration. Nothing a client sends becomes part of them.
- Images come from a fixed catalog of engine and version (`postgres:15`–`17`, `mysql:8.0`,
  `8.4`). A version outside it fails the instance; it never becomes an image reference.
- Containers are created without privileges, with `no-new-privileges`, with no capabilities or
  devices added, and with no bind mount of any host path (the only mount is the instance's named
  volume).
- A container publishes **no port on the host** unless an administrator has enabled external
  access for its instance, and then exactly one: the engine's own port, on a host port Aurora
  picked, bound to the address the server is configured with. See
  [External database access](#external-database-access). Otherwise it is reachable only on
  Aurora's Docker network.
- The API has no operation that takes a host path, an image, a Docker option, a command, a port or
  an address.

The database processes inside the containers run under the images' own defaults. Containers that
were created before this phase keep their settings until they are recreated.

## External database access

By default a database instance can be reached from Aurora's Docker network and from nowhere else.
An administrator can enable **external access** for an instance; Aurora then publishes that
instance's database port on a port of the Docker host. Nothing else exposes a database: not
creating an instance, not creating a database, and not upgrading Aurora. Instances that existed
before this feature are private after the upgrade.

```text
client ──► <bind address>:<host port> on the Docker host ──► the instance's database port
           (15432…16432)                                     (5432 PostgreSQL, 3306 MySQL)
```

### What Aurora controls, and what it does not

Aurora controls **Docker's port publishing**, and nothing beyond it. It does not open, close or
inspect the host's firewall (`ufw`, `iptables`, `nftables`), a cloud security group, a load
balancer, NAT or routing. *External access: Enabled* means the port is published on the address
below. Whether anyone can actually reach it is decided by that address and by what is in front
of the host, and that is the operator's to configure. Aurora never describes a database as
reachable from the internet, because it cannot know.

One thing about Docker matters here: **published ports bypass `ufw`** and similar host firewalls
on most Linux installations, because Docker writes its own `iptables` rules ahead of them. A rule
that blocks the port for everything else does not necessarily block a port Docker published. Bind
to a specific address, or filter in the `DOCKER-USER` chain or in the network in front of the
host; do not rely on `ufw deny` alone.

### The bind address

`ExternalAccess:BindAddress` is the address of the Docker host that every published database
port is bound to. It is a setting of the server; no request and no user can choose another.

| `BindAddress` | Reachable from | Use it when |
| --- | --- | --- |
| `127.0.0.1` (default) | the Docker host itself only | clients run on the host, or reach it through an SSH tunnel or a proxy on the host. This is the default because it exposes nothing to any network. |
| a private address, e.g. `10.0.0.5` | whatever can reach that interface | clients are on a private network or VPN the host is attached to |
| `0.0.0.0` | every network the host is on, the internet included if it has a public address | only with a firewall or security group in front that allows the port range from known addresses |

Changing the setting and restarting Aurora moves the published ports of the instances that have
external access to the new address; each of those database servers is restarted once for it.

With `0.0.0.0` Aurora does not know which address clients use. Set `ExternalAccess:AdvertisedHost`
to the host name or address to show them; without it the UI and the API give no host, and
connection strings have the placeholder `<server-address>`.

### Ports

| Setting | Default | Notes |
| --- | --- | --- |
| `ExternalAccess:BindAddress` | `127.0.0.1` | An IP address of the Docker host. |
| `ExternalAccess:AdvertisedHost` | empty | Host name or address shown to clients. Empty: the bind address, if it is a specific one. |
| `ExternalAccess:PortRangeStart` | `15432` | First host port that may be given to an instance. Never below 1024. |
| `ExternalAccess:PortRangeEnd` | `16432` | Last one. |

- An instance gets the lowest port of the range that no instance has on record and that nothing
  is published on in Docker. A request cannot name a port.
- One port belongs to one instance. A unique index in the system database refuses a second
  instance the same port, so two requests at the same moment cannot both get it; the one that is
  refused takes the next port.
- A port can be taken by something Docker does not list, a process on the host for instance.
  Docker then refuses to publish on it, and Aurora tries the next port (up to five), or answers
  `PORT_ALLOCATION_FAILED`.
- Disabling external access gives the port up; it may be given to another instance later. Open
  the firewall for the range, not for one port you expect an instance to keep.
- Invalid settings stop Aurora at startup with a message that names the setting.

### Enabling and disabling restarts the database

Docker cannot change what an existing container publishes. Enabling or disabling external access
therefore **replaces the instance's container and restarts its database server**:

1. the server is stopped in order and its container set aside;
2. a new container is created on the **same data volume**, with the new port configuration;
3. it is started, and Aurora waits until the database accepts connections;
4. the new container's labels, image, volume and published port are checked;
5. only then is the old container removed, and the change recorded.

The database is unavailable to all its clients from step 1 to step 3, usually a few seconds. The
data volume is never removed, recreated or replaced. If any step fails, the new container is
removed, the old one is put back and started, nothing is recorded, and the request fails with a
stable code (`DOCKER_PORT_CONFIGURATION_FAILED`, `PORT_ALREADY_IN_USE` handled by trying another
port, `DOCKER_UNAVAILABLE`). Aurora never records external access as enabled unless Docker has
the port published and the database is up behind it.

The change is refused while the instance is not `running`, and while a job is at work in it (a
database being created or deleted, a backup, a restore), because the restart would fail that job.
A job that starts during the restart can still fail and be retried.

### After a restart of Aurora

The instance's record is what counts. When Aurora starts it checks every running instance's
container against it: the owner label, the image, the volume, and now the published port and
its bind address.

- A container that matches is left alone; it is not recreated.
- A container that publishes something the record does not say, or does not publish what it says,
  is brought in line with the record by the same replacement as above. This covers a change that
  was interrupted by a crash, a reconfigured bind address, and a container altered behind Aurora's
  back. A port the record says is private does not stay published.
- A container that is not the instance's own is never touched, as before.

While an instance is being provisioned, a container left by an earlier attempt is adopted only if
it publishes exactly what the record says; otherwise provisioning fails with
`DOCKER_RESOURCE_CONFLICT` and the container is left as it is.

### Who may

| | viewer | operator | admin |
| --- | :---: | :---: | :---: |
| See whether and where an instance is reachable | ✔ | ✔ | ✔ |
| Enable or disable external access | | | ✔ |

Exposure is of the instance, like creating and deleting it, so it is an administrator's. All
databases of an instance share its server and therefore its port: enabling external access makes
every database in the instance reachable there for anyone who can reach the port and
authenticate.

### Passwords and encryption in transit

- **Aurora shows an instance's administrator password in one place only**: the one-time result
  of a completed rotation (see *Rotating an instance's password*). Everywhere else, in the UI
  and the API, with external access or without, it does not. Connection information is the host, the port, the database name
  and the user name. Connection strings are templates with the literal `<password>` where the
  password goes. No connection string with a password is built anywhere in Aurora's code that
  serves users, so none can be logged, rendered or returned.
- The only account an instance has is the engine's administrator (`postgres`, `root`), and its
  password is in Aurora's secret store. Aurora does not yet create application users or hand out
  that password. Getting credentials to a client is therefore outside Aurora for now: see *What
  is deliberately not included*.
- **That password can be rotated, and the new one is handed out once.** An operator or
  administrator can have Aurora replace it with a newly generated one
  (`POST /api/v1/instances/{id}/credentials/rotate`, or *Rotate password* on the instance's
  page). The old password stops working; the new one is stored encrypted and can be retrieved a
  single time. This is how a client outside Aurora gets a credential. See *Rotating an
  instance's password* below.
- **Aurora does not set up TLS for database connections.** The engines' own defaults apply:
  MySQL 8 offers TLS with a self-signed certificate; the PostgreSQL image does not enable it. On
  anything but `127.0.0.1` or a trusted private network, treat connections as unencrypted unless
  you have configured otherwise, and prefer a tunnel or a VPN.

## Backups on the local filesystem

- A backup's path is built by Aurora from three ids and a fixed extension under
  `Backups:Local:RootPath`, and is checked to lie under that root before use. No request can name
  a path.
- Directories are created with mode `0700` and files with `0600`: readable by the account Aurora
  runs under, and nobody else. Keep the root on a volume only that account can reach.
- A backup is written as `<file>.partial` and renamed into place only once it is complete and its
  checksum verified. A `.partial` file is never a backup.
- Restores stage the artifact in a directory of their own (`Backups:Restore:StagingPath`), named
  by the job id, and remove it afterwards.

## S3-compatible storage

- Object keys are `<prefix>/backups/instances/<id>/databases/<id>/<id>.<ext>`: the configured
  prefix and ids. No request can name a bucket or a key.
- Credentials are never logged and never returned. Requests are signed; the secret key itself is
  not sent.
- Use an `https://` endpoint. With a plain `http://` endpoint on another machine, backups travel
  unencrypted, and Aurora logs a warning when it first uses the storage.
- Aurora creates no presigned URLs and needs no public access. Keep the bucket private, and
  enable encryption at rest on the bucket.

### Minimum permissions

For the configured bucket, and nothing else:

| Permission | On | Used for |
| --- | --- | --- |
| `s3:PutObject` | `arn:aws:s3:::<bucket>/<prefix>/*` | Storing a backup. |
| `s3:GetObject` | `arn:aws:s3:::<bucket>/<prefix>/*` | Verifying a stored backup, and restoring. |
| `s3:AbortMultipartUpload` | `arn:aws:s3:::<bucket>/<prefix>/*` | Cleaning up a large upload that failed. |
| `s3:ListBucket` | `arn:aws:s3:::<bucket>` | `GET /health/storage`, which is one `HEAD` on the bucket. |

`s3:DeleteObject` is not needed: Aurora never deletes a backup. Without `s3:ListBucket`, backups
and restores still work, and `/health/storage` reports the storage as unhealthy; grant it if you
use that check, and do not widen anything else to make the check pass.

## Secrets

| Secret | Where it comes from | Where it is kept |
| --- | --- | --- |
| JWT signing key | `Authentication:Jwt:SigningKey` | Memory only |
| Bootstrap administrator password | `Authentication:BootstrapAdmin:Password` | Hashed, in `users` |
| User passwords | The API | Hashed (PBKDF2), in `users` |
| System database connection string | `ConnectionStrings:SystemDatabase` | Memory only |
| S3 access key and secret key | `Backups:S3`, or the AWS SDK's own resolution | Memory only |
| Instance administrator passwords | Generated by Aurora | Encrypted with Data Protection, in `instance_secrets` |

While an instance's password is being rotated, the password that is to replace it is kept in
the same table, encrypted the same way, until it has taken the old one's place. The one-time
result of a rotation adds no secret: it is read from the stored password when it is retrieved.

Supply configured secrets through the environment or a secret store, never through a settings
file that is committed or baked into an image. None of them is logged, returned by the API, put
in a metric, or passed on a command line. Persist the Data Protection key ring and keep it as
safe as the system database: without it the instance passwords cannot be decrypted, and with it
and the database they can.

### Rotating an instance's password

- **Generated, never chosen.** The new password is 32 letters and digits from the operating
  system's random number generator, like the first one. No request can supply one.
- **Disclosed once, in one response.** `POST /api/v1/instances/{id}/credentials/rotate/{jobId}/result`
  and the page behind *Show the new password* return the username and the new password of a
  rotation that completed. That is the only place a password crosses the application's
  boundary. It is in no other API response, page, address, redirect, cookie, job, log entry or
  error. A failed rotation is reported by a stable code and a fixed sentence; what the database
  driver said is not kept, because a server that rejects a statement may quote it.
- **Once means once.** The first request gets it; every later one, by anyone, gets
  `409 CREDENTIAL_RESULT_ALREADY_RETRIEVED`. Of requests made at the same moment the database
  lets exactly one through. Nothing is stored to make this possible but which job's result the
  stored password is, until when it may be given out, and when it was: three columns, no secret.
- **A failure cannot use it up.** Recording that the password was given out and decrypting it
  happen in one transaction that is committed last. If decrypting or preparing the answer
  fails, the record is rolled back and the password can still be retrieved.
- **For a limited time.** 15 minutes after the rotation completed by default
  (`Credentials:ResultTtlMinutes`, 1 to 1440). Expiry withdraws the offer and nothing else: the
  password keeps working. A newer rotation withdraws an unretrieved older result as soon as it
  is accepted.
- **Only a completed rotation.** One that failed, at any step, has no result; one that was
  interrupted gets its result when recovery has finished it.
- **Who may retrieve.** Any operator or administrator, not only the one who asked for the
  rotation. Who did is logged, by user id, without the password. Viewers cannot.
- **It is a `POST`**, because it changes what the next request gets, and it is answered with
  `Cache-Control: no-store`. A client must not log the response.
- **How it reaches the server.** PostgreSQL is sent the SCRAM-SHA-256 verifier computed by
  Aurora, the value the server stores, so the password itself never leaves Aurora and cannot
  appear in the server's statement log. MySQL is sent the password as a parameter of
  `ALTER USER`, which MySQL keeps out of its own logs, for every `root` account at once. Nothing
  is run through a shell and nothing is passed on a command line.
- **Recoverable.** The replacement is stored before the server is changed and promoted only
  after a new connection has got in with it; a job that is retried, or picked up after a
  restart, works out which of the two stored passwords the server accepts and continues from
  there. It never generates a second replacement while one is stored.
- **One at a time**, per instance, enforced by a unique index; and not while other work is
  going on in the instance.
- **Who may rotate.** Operators and administrators. Viewers see that the credential exists, its
  username and when it was last rotated.
- **What an operator has to know.** Anything outside Aurora that connects as `postgres` or
  `root` with the old password stops working until it is given the new one, which is retrieved
  once and then is the operator's to keep safe. A password that was shown and lost is replaced
  by rotating again; Aurora does not show it a second time. The instance's first password, the
  one it was created with, is never handed out: rotate once to get a credential. The
  container's environment (`docker inspect`) keeps showing the password the server was
  initialized with, which no longer works; if the container is later replaced, which enabling
  or disabling external access does, the new container's environment holds the password
  current at that time. Access to Docker on the host is, as before, access to that value.

## Errors and logs

A failure is answered with a stable error code and a fixed message. Responses never contain
stack traces, SQL, connection strings, file system paths, Docker or SDK error text, command
lines, or credentials; that holds in the development environment too. The detail an operator
needs is in the log, under the request id, and the log in turn never contains a password, a
password hash, a token, a signing key, an S3 key or a request body.

Without a valid token every path answers `401`, so the API cannot be mapped anonymously. To a
signed-in user, `404` means what it says: every signed-in user may read every resource, so there
is nothing to hide between them.

## OpenAPI

The UI's style guide (`/styleguide`) and the OpenAPI document (`/openapi/v1.json`) are served **in the development environment only**. In
production the route does not exist. The document describes bearer authentication and marks every
operation except login as requiring it; it contains no secrets. There is no Swagger UI.

## Health endpoints

`GET /health` and `GET /health/ready` are public so that load balancers can use them, and return
statuses only. `GET /health/storage` requires a signed-in user. See [monitoring.md](monitoring.md).

## What is deliberately not included

For database connectivity: TLS termination for database connections, a database proxy, SSH
tunnelling, VPN integration, firewall or security-group automation, per-database users, and a
way to reveal or rotate an instance's administrator password.

MFA, OAuth/OIDC and SSO; refresh tokens and token revocation; account lockout and CAPTCHA;
distributed or Redis-backed rate limiting; a WAF or API gateway; an audit-log subsystem; SIEM
integration and intrusion detection; malware or container-image scanning; certificate management;
and integration with a secrets manager. These are reasonable later, if a deployment needs them.
