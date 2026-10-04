# Authentication and authorization

This document is about users, roles and the REST API's bearer tokens. Signing in to the UI, with
a cookie instead of a token, is described in [ui.md](ui.md); the users and roles are the same.

Aurora is a single self-hosted installation with user accounts. Every management request is made
by a signed-in user, and what the user may do follows from one of three roles. There are no
tenants, organizations or per-resource owners: a role applies to the whole installation.

```text
POST /api/v1/auth/login  ──►  access token (JWT)
                                   │
        Authorization: Bearer …    ▼
request ──► authentication ──► authorization (policy by role) ──► endpoint
               401 if no              403 if the role
               valid token            is not enough
```

Authentication and authorization are ASP.NET Core's own (`JwtBearer`, authorization policies).
Which role may do what is decided in one place, `AuroraPolicies`, and applied at the endpoints;
the services behind them never look at roles.

Background work is not affected. The job worker and the backup scheduler are the application's
own: they need no user and no token, jobs store nothing about users, and jobs that are recovered
after a restart or created by a schedule run as before.

## First start

Two things are needed before anyone can sign in: a signing key, and the first administrator.

```bash
# A random signing key, kept secret. Required: Aurora does not start without one.
export Authentication__Jwt__SigningKey="$(openssl rand -base64 48)"

# The first administrator. Only used while no administrator exists.
export Authentication__BootstrapAdmin__Username="admin"
export Authentication__BootstrapAdmin__Password="<initial-password>"
```

Start Aurora. The log says `Bootstrap administrator admin created`. Sign in, and then:

1. Remove the two `BootstrapAdmin` settings from the environment. They are not needed again.
2. Preferably, change that administrator's password (`PUT /api/v1/users/{id}`), so the password
   that was in the environment is no longer a valid one.

### What bootstrap does and does not do

- It runs at startup, and only if **no administrator exists**. Once one does, the settings are
  ignored, whatever they say: an existing account is never overwritten, its password is never
  reset, and no second administrator is added.
- Without the settings, no account is created. There is no default user and no default password.
  If no administrator exists and none is configured, Aurora starts, logs a warning that says so,
  and nobody can sign in.
- If a user with the bootstrap username exists and is not an administrator, nothing happens (an
  error is logged). Bootstrap never promotes an existing user.
- The password is hashed like any other. It is never logged and never returned by the API.
- There is no registration endpoint. After the first administrator, users are created by an
  administrator.

If every administrator's password is lost, there is deliberately no way in through the API. An
operator with access to the system database can delete the administrators' rows from `users` and
bootstrap again.

## Configuration

```text
Authentication:
  Jwt:
    Issuer: aurora-db-manager
    Audience: aurora-db-manager-api
    SigningKey: <secure-secret>
    AccessTokenLifetimeMinutes: 30

  BootstrapAdmin:
    Username: admin
    Password: <initial-password>
```

| Setting | Default | Notes |
| --- | --- | --- |
| `Authentication:Jwt:SigningKey` | none | **Required.** At least 32 bytes. A secret. |
| `Authentication:Jwt:Issuer` | `aurora-db-manager` | |
| `Authentication:Jwt:Audience` | `aurora-db-manager-api` | |
| `Authentication:Jwt:AccessTokenLifetimeMinutes` | `30` | 1 to 1440. |
| `Authentication:BootstrapAdmin:Username` | none | Both or neither. 3–64 letters, digits, `.`, `-`, `_`. |
| `Authentication:BootstrapAdmin:Password` | none | Both or neither. 12–128 characters. A secret. |

As environment variables the names use double underscores: `Authentication__Jwt__SigningKey`.

**Secrets.** The signing key and the bootstrap password are secrets. Supply them through the
environment or a secret store (for development, `dotnet user-secrets`), not through a settings
file that is committed or baked into an image. Neither has a value in the `appsettings` files
that ship with Aurora, and neither is ever logged or returned.

**The signing key.** Whoever knows it can issue a token for any user and any role. There is no
built-in or development key: if it is missing or shorter than 32 bytes, Aurora refuses to start
and names the setting (not the value). Changing the key invalidates every token issued so far;
users sign in again. All instances of one installation must use the same key.

## Signing in

```http
POST /api/v1/auth/login
Content-Type: application/json

{ "username": "admin", "password": "<password>" }
```

```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIs…",
  "tokenType": "Bearer",
  "expiresAt": "2026-03-10T10:30:00Z"
}
```

Send the token with every other request:

```http
GET /api/v1/instances
Authorization: Bearer eyJhbGciOiJIUzI1NiIs…
```

Sign-in attempts are limited per client address (10 a minute by default). Beyond the limit the
answer is `429 TOO_MANY_REQUESTS` with a `Retry-After` header, whatever the credentials.

The username is not case sensitive. A wrong password, a username that does not exist and a
disabled user are all answered with the same response, and take the same work to answer, so the
login endpoint does not reveal which usernames exist:

```json
{ "error": { "code": "INVALID_CREDENTIALS", "message": "Invalid username or password." } }
```

### Access tokens

A token is a JWT signed with HMAC-SHA256. It carries the user's id (`sub`), username (`name`) and
role (`role`), plus issuer, audience and validity, and nothing else: no password, no hash, no
credential of any kind.

A token is accepted only if it is signed with the configured key and that algorithm, was issued
by and for this installation, and has not expired. It is read from the `Authorization` header
only, never from a query string or a cookie.

**Tokens are not revoked, and there are no refresh tokens.** A token stays valid, with the role
it was issued for, until it expires, even if the user is disabled, deleted or given another role
in the meantime. This is why the default lifetime is 30 minutes: that is the longest a change to
a user can take to be fully effective. A disabled user cannot sign in again. To cut every token
off at once, change the signing key. When a token expires, the client signs in again.

Clients must treat the token as a credential: keep it out of logs, URLs and source control.

## Roles

Each role includes everything the one below it may do.

| | viewer | operator | admin |
| --- | :---: | :---: | :---: |
| View instances, databases, backups, schedules, jobs | ✔ | ✔ | ✔ |
| View monitoring: summary, instance health, `/health/storage` | ✔ | ✔ | ✔ |
| Create and delete databases | | ✔ | ✔ |
| Create backups, restore backups | | ✔ | ✔ |
| Create, change and delete backup schedules | | ✔ | ✔ |
| Create and delete instances | | | ✔ |
| Manage users | | | ✔ |

## Endpoints

| Endpoint | Who |
| --- | --- |
| `GET /health`, `GET /health/ready` | public |
| `POST /api/v1/auth/login` | public |
| `GET /health/storage` | viewer and above |
| `GET /api/v1/instances`, `…/{id}`, `…/{id}/health` | viewer and above |
| `POST /api/v1/instances`, `DELETE /api/v1/instances/{id}` | admin |
| `GET /api/v1/instances/{id}/databases`, `GET /api/v1/databases/{id}` | viewer and above |
| `POST /api/v1/instances/{id}/databases`, `DELETE /api/v1/databases/{id}` | operator and above |
| `GET /api/v1/databases/{id}/backups`, `GET /api/v1/backups/{id}` | viewer and above |
| `POST /api/v1/databases/{id}/backups`, `POST /api/v1/backups/{id}/restore` | operator and above |
| `GET /api/v1/databases/{id}/backup-schedule` | viewer and above |
| `POST`, `PUT`, `DELETE /api/v1/databases/{id}/backup-schedule` | operator and above |
| `GET /api/v1/jobs`, `GET /api/v1/jobs/{id}` | viewer and above |
| `GET /api/v1/monitoring/summary` | viewer and above |
| `/api/v1/users`, every method | admin |

The two health probes are public so that a load balancer or orchestrator can use them without
credentials; they return statuses and nothing else. `/health/storage` is not a probe (each call
is a request to the object store) and is for signed-in users.

Anything that is not explicitly public needs a signed-in user. An endpoint added without a policy
is not thereby open: it falls back to "any signed-in user with a known role".

### 401 and 403

| Status | Code | When |
| --- | --- | --- |
| `401` | `UNAUTHORIZED` | No token, or a token that is not valid. |
| `401` | `INVALID_CREDENTIALS` | Login only: wrong username or password, or a disabled user. |
| `403` | `FORBIDDEN` | A valid token whose role does not allow the request. |
| `429` | `TOO_MANY_REQUESTS` | Login only: too many attempts from this client address. |

Both use the API's standard error body. Why a token was not accepted (missing, malformed, expired,
wrong signature) is not said, in the body or in the `WWW-Authenticate` header. A request without
a valid token gets `401` for any path, so paths cannot be probed either.

## User management

Administrators only.

| Method | Path | Result |
| --- | --- | --- |
| `GET` | `/api/v1/users` | `200`, paged (`page`, `pageSize` up to 100), newest first |
| `POST` | `/api/v1/users` | `201` with the user |
| `GET` | `/api/v1/users/{id}` | `200` with the user |
| `PUT` | `/api/v1/users/{id}` | `200` with the user |
| `DELETE` | `/api/v1/users/{id}` | `204` |

Create:

```json
{ "username": "olivia", "password": "<password>", "role": "operator", "enabled": true }
```

Update. `role` and `enabled` are required and set to what the request says; `password` is
optional, and changes the password only when it is given (this is how a password is reset):

```json
{ "role": "viewer", "enabled": false, "password": "<new-password>" }
```

A user as returned. The password hash is never part of any response:

```json
{
  "id": "0199…",
  "username": "olivia",
  "role": "operator",
  "enabled": true,
  "createdAt": "2026-03-10T10:00:00Z",
  "updatedAt": "2026-03-10T10:00:00Z"
}
```

Rules:

- **Username**: 3 to 64 letters, digits, dots, dashes and underscores; no whitespace. Unique
  whatever its case (`Olivia` and `olivia` are one name). It cannot be changed.
- **Password**: 12 to 128 characters.
- **Role**: `admin`, `operator` or `viewer`.
- **Disabled** users cannot sign in.

| Error code | Status | When |
| --- | --- | --- |
| `USER_NOT_FOUND` | 404 | No such user. |
| `USERNAME_ALREADY_EXISTS` | 409 | The name is taken, in any case. |
| `LAST_ADMINISTRATOR` | 409 | See below. |
| `VALIDATION_FAILED` | 400 | Username, password or role do not meet the rules. |

**The last administrator.** The only enabled administrator cannot be deleted, disabled or given
another role: an installation without one could not be managed, and nobody would be left to fix
it. Their password can still be changed. With a second enabled administrator, either can go.

## Passwords

Passwords are hashed with ASP.NET Core's password hasher: PBKDF2 with HMAC-SHA512, 210,000
iterations and a random salt per password, compared in constant time. Only the hash is stored.
A plaintext password is never written to the database, never logged, and never returned; the
hash is never returned either. A hash made with older parameters still works and is replaced
with a current one the next time the user signs in.

## Logging

Authentication keeps the request correlation of [monitoring.md](monitoring.md): failed and
refused requests carry their `X-Request-Id` like any other.

| Event | Level | Fields |
| --- | --- | --- |
| `User {UserId} signed in` | information | `UserId`, `Role` |
| `Login failed` | warning | `UserId` when the user exists, and a `Reason` (`unknown_user`, `wrong_password`, `user_disabled`) |
| User created, updated, deleted | information | `UserId`, the acting administrator's id, what changed |
| Every request | information | …and the id of the user who made it, or `anonymous` |

The reason a login failed is in the log for operators and is not told to the client. The username
that was tried is not logged for an unknown user: what gets typed into a username field by mistake
is often a password.

Never logged: passwords, password hashes, access tokens, the signing key, and the bootstrap
password.

## Transport

Aurora does not terminate TLS itself in a typical deployment. Run it behind a reverse proxy that
does, and **serve it over HTTPS only in production**: a bearer token sent over plain HTTP can be
read and reused by anyone on the path.

- Aurora redirects HTTP to HTTPS when it knows its HTTPS port.
- Behind a proxy, list the proxy in `Security:TrustedProxies` so Aurora sees the original scheme
  and client address. Do **not** set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`: it trusts forwarded
  headers from every client, and Aurora refuses to start with it.

Reverse proxies, HTTPS, the login rate limit and the rest of how the API is exposed are described
in [security.md](security.md).

## Not included

By design, in this phase: multi-tenancy, organizations and resource ownership; refresh tokens and
token revocation; OAuth, OpenID Connect and social login; MFA; API keys and service accounts;
invitations, email verification and password-reset emails; an audit log; and account lockout.
Sign-in attempts are rate limited per client address; see [security.md](security.md).
