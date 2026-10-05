# The UI

Aurora has a server-rendered administration UI, built with ASP.NET Core Razor Pages in
`src/AuroraDbManager.Web`. This document describes its foundation, how it relates to the API, how
users sign in to it, how it is put together and how to build on it, and the pages that manage
[instances and databases](#instances-and-databases). The pages that manage backups, schedules and
users come in later phases.

```text
Browser      ──►  Web  ──►  cookie authentication  ──►  pages  ──┐
                                                                 ├──►  application services
API clients  ──►  API  ──►  JWT authentication     ──►  REST   ──┘
```

## Why Razor Pages

Aurora is infrastructure software for a handful of operators. Its UI is forms, tables and status,
which a server renders well. Razor Pages gives that with no JavaScript framework, no client-side
state, no build step and no Node at build time or at run time: the UI is part of the .NET
application, and is deployed as part of it. Every page works with JavaScript turned off.

## Architecture

```text
            AuroraDbManager.Web                 AuroraDbManager.Api
        ┌──────────────────────────┐        ┌─────────────────────────┐
        │ Razor Pages  +  REST API │        │        REST API         │
        │ (cookie)        (JWT)    │        │         (JWT)           │
        └────────────┬─────────────┘        └────────────┬────────────┘
                     └───────────────┬───────────────────┘
                                     ▼
                            AuroraHost (composition)
                                     ▼
              Application services · Domain · Infrastructure
```

There are two hosts, built from the same pieces:

- **`AuroraDbManager.Web`** — the UI, and with it the whole of Aurora: the application services
  and background workers run in this process, and the REST API is served next to the pages. One
  process is a complete installation. This is the host to run.
- **`AuroraDbManager.Api`** — the REST API alone, for a headless installation.

What a host is made of is defined once, in `AuroraHost` (in the API project): `AddAuroraCore` is
everything that is not about HTTP, `AddAuroraApi` is the REST API, and the `UseAurora…` methods
are the request pipeline. The Web host adds Razor Pages, cookie authentication and its error
pages on top. The API project still contains the application, domain and infrastructure code; the
Web project references it and contains none.

### The Web/API boundary

**Pages call the application services directly.** A page model takes `InstanceService`,
`JobService`, `MonitoringSummaryService` and so on from dependency injection, the same services
the API's controllers call. A page does not call the API over HTTP, and there is no API client in
the Web project.

```text
Razor Page  ──►  application service  ──►  domain / infrastructure
Controller  ──►  application service  ──►  domain / infrastructure
```

The rule for every page, now and later: **no business rule lives in a page.** A page reads
through a service and renders, or passes a request to a service and shows the result. Validation,
state transitions, what conflicts with what, who may be the last administrator: all of that is in
the services already, and stays there. The API's request and response types are the services'
contracts, so pages use them too.

The API is unchanged by the UI. No endpoint was added, removed or renamed for it, none depends on
a page, and the API host has no knowledge of the UI at all.

## Authentication

The UI and the API identify the same users, with the same passwords and the same three roles.
They differ in how a signed-in client proves who it is, because a browser and a script are
different kinds of client.

| | UI (`/…`) | API (`/api`, `/health`, `/openapi`) |
| --- | --- | --- |
| Client | a person's browser | scripts, tools, other software |
| Signs in at | the form at `/login` | `POST /api/v1/auth/login` |
| Gets | a session cookie | an access token (JWT) |
| Sends it | automatically | in `Authorization: Bearer …`, on purpose |
| Credentials checked by | `AuthService.AuthenticateAsync` | the same |
| Not signed in | redirected to `/login` | `401` JSON |
| Not allowed | `403` page | `403` JSON |

**No token is ever given to the browser.** The UI stores nothing in `localStorage` or
`sessionStorage`, and its script makes no requests.

**Each side takes only its own credential.** In the Web host a request to the API is
authenticated by its bearer token and nothing else; a request for a page by the session cookie
and nothing else. The cookie is deliberately not accepted by the API: a browser attaches cookies
by itself, also to a request another site makes it send, and API requests carry no antiforgery
token. A bearer token has to be attached on purpose, so it cannot be made to ride along.

### Signing in and out

- `/login` is a form: username, password, *Sign in*. A wrong password, an unknown username and a
  disabled user get the same message, *Invalid username or password*, and nothing that was typed
  is shown again.
- After signing in the user goes to the page they were on their way to, if it is a page of this
  application, and to the overview otherwise. A sign-in link cannot send a user to another site.
- A signed-in user who opens `/login` is sent to the overview.
- Sign-in attempts through the form and through the API share one rate limit per client address
  (see [security.md](security.md)). Beyond it the UI shows a page saying when to try again.
  Loading the form does not count as an attempt.
- Signing out is a `POST` to `/logout` with an antiforgery token. A `GET` changes nothing, so no
  link and no other site can sign a user out.

### The session cookie

| Property | Value |
| --- | --- |
| Name | `__Host-aurora.session` (`aurora.session` in development) |
| `HttpOnly` | always: scripts cannot read it |
| `Secure` | always, except in development and where `Security:HttpsRedirection` is off |
| `SameSite` | `Lax`: not sent with requests other sites make, except when following a link here |
| Scope | this host only, every path (`__Host-` makes the browser enforce that) |
| Lifetime | a session cookie, valid for at most `Web:SessionHours` (8) from signing in; not extended by use |
| Contents | encrypted; the user's id, name and role |

The cookie holds no password, no token and no other secret, and it is not the source of truth:
**every page request looks the user up.** A user who is disabled or deleted is signed out at
their next request, and a changed role applies from the next request. (An API token, being
stateless, keeps its role until it expires; that is why tokens last minutes and sessions hours.)

The cookie is encrypted with ASP.NET Core Data Protection. Keep the key ring persistent across
restarts and deployments, or every restart signs everyone out.

## Authorization

The UI uses the roles and the policies the API uses (`AuroraPolicies`); it has none of its own.

- Every page needs a signed-in user unless it says otherwise. Three do: `/login`, `/logout` and
  the error page.
- A page for administrators is marked with the admin policy, exactly like the users API.
- **What the UI shows is not what is allowed.** The navigation offers *Users* only to
  administrators, read from the claims of the request with no extra lookup, but that is a
  courtesy. A viewer who types `/users` gets `403` from the same policy that would refuse the
  API call. Later phases hide buttons the same way and rely on it just as little: the service or
  the policy behind the button decides.

| Section | viewer | operator | admin |
| --- | :---: | :---: | :---: |
| Overview, Instances, Backups, Schedules, Jobs, Monitoring | ✔ | ✔ | ✔ |
| Users | | | ✔ |

What each role may do with instances and databases is in
[Instances and databases](#who-may-do-what).

## Project structure

```text
src/AuroraDbManager.Web/
├── WebProgram.cs            the host: what it adds to AuroraHost
├── WebOptions.cs            the Web section of the configuration
├── Authentication/          cookie sign-in, and the choice between cookie and bearer
├── Components/              what pages are made from, in C#: navigation, badges, page header…
├── Pages/
│   ├── Shared/              layouts and partials
│   │   ├── _Layout.cshtml         the application shell
│   │   ├── _BareLayout.cshtml     sign-in, and errors for someone who is not signed in
│   │   ├── _PageHeader.cshtml     breadcrumbs, title, status, description, primary action
│   │   ├── _StatusBadge.cshtml    a status: mark, word, colour
│   │   ├── _Alert.cshtml          a message
│   │   ├── _EmptyState.cshtml     what a page shows when it has nothing to show
│   │   ├── _Time.cshtml           an instant, in UTC
│   │   ├── _Flash.cshtml          the message a change left for the next page
│   │   ├── _Pager.cshtml          which part of a list is shown, and the way to the rest
│   │   ├── _InProgress.cshtml     work is in progress: refresh
│   │   ├── _DatabaseTable.cshtml  the databases of an instance
│   │   ├── _ConnectionEndpoints.cshtml  where a server is reached, inside and outside Docker
│   │   ├── _RecentJobs.cshtml     the last few operations on an instance or a database
│   │   ├── _Icon.cshtml           the icons, inline
│   │   └── _ConfirmDialog.cshtml  the one confirmation dialog
│   ├── Index.cshtml               the overview
│   ├── Login / Logout / Error
│   ├── Instances/                 list, create, details, delete, external access
│   │   └── Databases/             an instance's databases: list, create, details, delete
│   ├── Jobs, Users                read-only lists
│   ├── Backups, Schedules, Monitoring   placeholders
│   └── Styleguide.cshtml          every building block (development only)
└── wwwroot/
    ├── css/aurora.css
    ├── js/aurora.js
    └── favicon.svg
```

## Building a page

Every page has the same shape:

```text
Page title
One sentence of context                      [ Primary action ]
──────────────────────────────────────────────────────────────
Main content: a table, a form, or an empty state
```

```cshtml
@page "/instances"
@model IndexModel
@{ ViewData["Title"] = "Instances"; }

<partial name="_PageHeader" model="@(new PageHeader("Instances", "The servers this installation runs."))" />

@if (Model.Instances.Items.Count == 0)
{
    <partial name="_EmptyState" model="@(new EmptyState("No instances yet", "…", "instances"))" />
}
else
{
    <div class="table-wrap">
        <table class="table"> … <partial name="_StatusBadge" model="@StatusBadge.For(instance.Status)" /> … </table>
    </div>
}
```

Open **`/styleguide`** in a development run to see every building block rendered: badges,
buttons, alerts, a form with its validation and confirmation, a table, and the empty and loading
states. It is not served outside development.

- **Status** is never colour alone. `StatusBadge.For(…)` maps each of the application's statuses
  to a word and one of five tones (success, progress, neutral, warning, danger); each tone has
  its own mark as well as its own colour.
- **Tables** are plain HTML tables with `class="table"` inside a `table-wrap`, which scrolls
  sideways on a narrow screen. Give each a `<caption>` and `scope="col"` headers. Lists show one
  page from the service; paging controls come with the feature phases.
- **Forms** post back to their page. Give every input a `<label>`, hints through
  `aria-describedby`, and errors next to the field with `asp-validation-for`. Validation is the
  server's: the page model's attributes for shape, the application service for everything else.
  Client-side attributes such as `required` are a convenience only.
- **Changes are `POST`s with an antiforgery token.** Razor Pages validates the token on every
  `POST` by default and the form tag helper adds it; this is not turned off anywhere. Never
  change state in a `GET`.
- **Confirmation**: `<form data-confirm="Delete this instance?" data-confirm-action="Delete">`
  asks first, in the shared dialog. Without JavaScript the form is submitted directly; the
  server's own checks are what protect the data either way.
- **Errors from a service** are shown with `_Alert`, using the service's stable error code and
  message. They are written for clients and are safe to show.
- **A message for the next page** ("Instance creation started.") is left with `Announce(…)` on a
  `ResourcePageModel` and shown once, by the layout, on the page the change redirects to. It
  travels in an encrypted, `HttpOnly`, `SameSite=Strict` cookie (`aurora.flash`), never in an
  address, and is a sentence, never a secret. There is no client-side notification system.
- **Breadcrumbs** for a page under another: `new PageHeader(…) { Breadcrumbs = [new Crumb("Instances",
  Routes.Instances), new Crumb(instance.Name)] }`.

## Instances and databases

These pages are the control panel: they list, create, inspect and delete instances and the
databases inside them. They are a presentation of `InstanceService`, `InstanceHealthService`,
`DatabaseService` and `JobService`, the services behind `/api/v1/instances`, `/api/v1/databases`
and `/api/v1/jobs`, and of nothing else: no page touches EF Core or Docker, and no rule about
instances or databases was added to, or copied into, the Web project.

```text
Razor Page  ──►  InstanceService / DatabaseService / InstanceHealthService / JobService
                        ──►  domain  ──►  infrastructure (system database, job queue, Docker)
```

### Pages

| Address | Page | Reads and calls |
| --- | --- | --- |
| `/instances` | The instances, 20 to a page (`?page=2`) | `InstanceService.ListAsync` |
| `/instances/create` | The form that creates one | `InstanceService.CreateAsync` |
| `/instances/{id}` | One instance: overview, health, connection, its databases, recent operations | `InstanceService.GetAsync`, `InstanceHealthService.GetAsync`, `InstanceConnectivityService.GetAsync`, `DatabaseService.ListAsync`, `JobService.ListAsync` |
| `/instances/{id}/delete` | The question, and the form that deletes | `InstanceService.DeleteAsync` |
| `/instances/{id}/external-access` | The question, and the forms that enable and disable external access | `InstanceConnectivityService.EnableAsync`, `DisableAsync` |
| `/instances/{id}/databases` | The instance's databases, 20 to a page | `DatabaseService.ListAsync` |
| `/instances/{id}/databases/create` | The form that creates one | `DatabaseService.CreateAsync` |
| `/instances/{id}/databases/{databaseId}` | One database: overview, connection, recent operations | `DatabaseService.GetAsync`, `InstanceConnectivityService.GetForDatabaseAsync`, `JobService.ListAsync` |
| `/instances/{id}/databases/{databaseId}/delete` | The question, and the form that deletes | `DatabaseService.DeleteAsync` |

Ids are GUIDs; anything else in their place is no page. A database is always addressed under its
instance, and a database of another instance is not found there. There is no top-level
*Databases* section: nothing lists databases across instances, in the application or in the API,
so they are reached through *Instances*.

### Who may do what

| | viewer | operator | admin |
| --- | :---: | :---: | :---: |
| See instances, their details, health and databases | ✔ | ✔ | ✔ |
| Create and delete a database | | ✔ | ✔ |
| Create and delete an instance | | | ✔ |
| Enable and disable external access | | | ✔ |

The pages that create and delete carry the policy of the API endpoint that does the same
(`[Authorize(Policy = AuroraPolicies.Admin)]` or `…Operator`), for the form and for its
submission alike. A user without the role gets the `403` page, whether they followed a link,
typed the address or sent the form by hand. Buttons a role cannot use are not shown, and that is
only a courtesy (`Offered` decides what is shown; the policy decides what is done).

### Changes

- **Every change is a `POST` with an antiforgery token**, and a `GET` changes nothing, whatever
  it carries in its address. Without a valid token the answer is the `400` page.
- **Deleting asks first, on a page of its own.** The *Delete* buttons are links to
  `…/delete`, which says what will be removed (for an instance: its server, all its data and how
  many databases) and that it cannot be undone, with *Cancel* and one button that does it. The
  page works without JavaScript, pre-selects nothing, and asks nothing to be typed.
- **Validation is the application's.** The instance form fills in a `CreateInstanceRequest` and
  checks it with that class's own rules, the ones the API applies, so limits on names, CPU,
  memory and storage are the API's limits. The database form passes the name to
  `DatabaseService`, whose `DatabaseName` rule answers. Messages appear at the fields, what was
  typed is kept, and the response is `422`.
- **Engines and versions are the image catalog's.** The form offers every engine with the
  versions `DockerImageResolver` can run, as one choice, and accepts nothing else. The UI has no
  list of its own.
- **A refusal is shown where it happened.** When a service says no (the instance is still
  provisioning, a backup is running, the name is taken, Docker cannot be reached) the page comes
  back with the API's stable error code and sentence, and the API's status (`409`, or `503`).
- **Submitting twice.** A submitted form disables its button. That is a convenience; what makes
  a repeat safe is the application: a second database of the same name is refused
  (`DATABASE_ALREADY_EXISTS`), as is a second delete (`DATABASE_DELETING`, or *not found* for an
  instance). Creating an instance has no such key, here or in the API: two submissions that both
  arrive are two instances.

### Work that takes a while

Only deleting an instance happens within the request. Everything else is accepted and then done
by a job, and the pages say exactly that:

| Action | What the application does | What the user sees |
| --- | --- | --- |
| Create instance | stores it as `provisioning` with a `provision_instance` job | *Instance creation started. The instance is being provisioned.*, on the instance's page, status **Provisioning** |
| Create database | stores it as `creating` with a `create_database` job | *Database creation started.*, on the database's page, status **Creating** |
| Delete database | marks it `deleting` with a `delete_database` job | *Deletion of database … started.*, on the list, status **Deleting**; it disappears when the job is done |
| Delete instance | removes the server and the data, then the record | *Instance … was deleted.*, on the list |

No page says *created* or *deleted* before the application does. A status is always the one on
record at the time the page was rendered. While something on a page is on its way, the page shows
*Work is in progress* with a **Refresh** link, and, with JavaScript, reloads itself every five
seconds: only while it is visible, never under an open menu or a submitted form, and at most 60
times in a row. A reload is an ordinary request, so an ended session leads to the sign-in page.
There is no polling of an endpoint, no SignalR and no client-side state.

What is offered follows the status on record: no *Delete* for an instance that is provisioning,
no *Create database* unless the instance is running, no *Delete* for a database that is not
ready. This is not a second set of rules; a request that arrives anyway is answered by the
service, and so is one the page could not have known about, such as a backup in progress.

### Health

The *Health* section of an instance's page is `InstanceHealthService.GetAsync`, the check behind
`GET /api/v1/instances/{id}/health`, made once when the page is rendered and repeated by *Check
again* (a link to the page). It is shown as **Healthy**, **Degraded** or **Unhealthy** with a
sentence, whether the server is running and whether connections are accepted. Docker details
are not part of it. Health is an observation and changes nothing: an
instance can be *Running* on record and *Unhealthy* in fact, and the page shows both. While an
instance is being provisioned there is no server to check, and the page says so rather than
reporting it as unhealthy.

### Connection

An instance's page and each database's page have a **Connection** section. It is
`InstanceConnectivityService`, the service behind `GET /api/v1/instances/{id}/connection` and
`GET /api/v1/databases/{id}/connection`; the pages add nothing to what it says.

- **From inside the Docker network**, always: the network's name, the host name the server
  answers to there (the instance's container name, which is what another container on that
  network connects to), the engine's port, and the user name.
- **From outside the Docker network**: *External access: Disabled* for every instance until an
  administrator enables it; then the host, the host port, the address the port is bound to with
  what that means (*this server only*, *one network interface*, *every network interface*), the
  protocol and the user name.
- On a database's page, in addition, the database name and **connection string templates**, such
  as `postgresql://postgres:<password>@127.0.0.1:15432/app`. The password is a placeholder.

A *Copy* button next to each block copies its text. It is progressive enhancement: hidden unless
script runs in a context where the browser allows copying (HTTPS or localhost), and the text is
on the page to be selected either way.

**The pages say what Aurora knows.** *Enabled* means the port is published in Docker on that
address. The pages never say a database is reachable from the internet or from any particular
place: that depends on the server's firewall and network, which Aurora does not configure
([security.md](security.md#external-database-access)).

### Enabling and disabling external access

Administrators see *Enable external access* (or *Disable…*) in an instance's Connection section
while the instance is running. It leads to `/instances/{id}/external-access`, a page that asks
before anything happens:

- what will be exposed, on which bind address and what that address means;
- that Aurora publishes a port in Docker and does not touch any firewall;
- that every database of the instance becomes reachable on that port;
- that **the database server is restarted**, with its data kept, and is briefly unavailable.

Only its form changes anything: a `POST` with an antiforgery token to `?handler=Enable` or
`?handler=Disable`. The request returns when the server is running with the new configuration,
which can take a while, so the button says *Restarting the database server…* meanwhile. The page
it leads to says *External access enabled on host port 15432. The database server was
restarted.*, and says it only then. Aurora picks the port; the form has no field for one.

If the service refuses (the instance is not running, a job is at work in it, access is already
in the requested state) or Docker fails, the confirmation page comes back with the stable code
and sentence and the API's status (`409` or `503`), nothing was changed, and no success is shown.

### Credentials

No page shows, embeds or logs a credential. The administrator password of an instance stays in
the secret store, as it does for the API; the pages never ask for it. Connection strings on the
pages are templates with `<password>` in place of the password, and the only hidden field of any
form is the antiforgery token.

## Styling

One hand-written stylesheet, `wwwroot/css/aurora.css`, with design tokens as CSS custom
properties and a small set of component classes. Tailwind was considered and not used: it needs a
build step (Node or a platform-specific binary) for something a few hundred lines of CSS do
here, and a UI with no build step is one less thing to break. There is no component library and
no web font; the UI uses the system font.

- Light and dark follow the operating system (`prefers-color-scheme`).
- Desktop first. Below about 830 px the sidebar becomes a panel behind a menu button, tables
  scroll sideways, and cards stack.
- No inline styles and no inline scripts, anywhere: the content security policy forbids them, and
  a test checks every page.
- Animation is one spinner, and it stops for users who prefer reduced motion.

### JavaScript

`wwwroot/js/aurora.js` is progressive enhancement, well under two hundred lines: the menu button
on small screens, closing the account menu, marking a submitted form as busy so it cannot be sent
twice, the confirmation dialog, reloading a page that shows work in progress (see
[Work that takes a while](#work-that-takes-a-while)), and the *Copy* buttons of the Connection
sections, which write text that is already on the page to the clipboard. It stores nothing, makes no request of its
own, and nothing depends on it. A later phase may add live updates (SignalR) for job and instance
status; nothing has been prepared for that yet, on purpose.

## Errors

Failures on a page are answered with a page; failures in the API with JSON, as before.

| Situation | What the user sees |
| --- | --- |
| Not signed in | redirect to `/login`, returning to the page afterwards |
| Not allowed (`403`) | *You don't have permission*, at the address that was asked for |
| Not found (`404`) | *Page not found*, with a way back; *Instance not found* or *Database not found* where a page looked for one |
| Missing or stale antiforgery token (`400`) | *That request could not be processed* |
| A conflict reported by a service (`409`) | the service's message, on the page that made the request |
| Invalid input (`422`) | messages at the fields, on the form, with what was typed |
| A list page that is not a page (`?page=0`) (`400`) | *That request could not be processed* |
| An instance's server cannot be removed (`503`) | the service's message, on the delete page; nothing was deleted |
| External access cannot be changed (`409`, `503`) | the service's message, on the confirmation page; nothing was changed |
| Too many sign-in attempts (`429`) | *Too many attempts*, and when to try again |
| Anything unexpected (`500`) | *Something went wrong* |

Every error page shows the **request ID**, the same one that is in the `X-Request-Id` response
header and on every log line of the request ([monitoring.md](monitoring.md)), so a user can
report a problem and an operator can find it. No error page ever shows an exception, in any
environment.

## Security

The Web host has everything the API host has ([security.md](security.md)): trusted proxies,
HTTPS redirection and HSTS, the login rate limit, the request size limit, request IDs, and the
same logging rules. What is specific to the UI:

- **Content security policy.** API responses keep `default-src 'none'`. Pages get a policy that
  allows exactly what a page needs and no more:

  ```text
  default-src 'none'; style-src 'self'; script-src 'self'; img-src 'self'; font-src 'self';
  connect-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'
  ```

  There is no `unsafe-inline` and no `unsafe-eval`, so there are no nonces or hashes to manage:
  pages simply contain no inline script or style.
- **Antiforgery** on every state-changing request, with its own `HttpOnly`, `SameSite=Strict`
  cookie.
- **No credentials in pages.** Instance passwords are never rendered, put in a hidden field, an
  address, a message or a log.
- **Cookies** as described above.
- **Pages are not cached** (`Cache-Control: no-store`): nothing is left behind after signing out.
  Styles and scripts are public and cached, with a version in their address.
- **Output is encoded.** Razor encodes everything a page prints; no page uses raw HTML.
- **Framing is refused** (`frame-ancestors 'none'`, `X-Frame-Options: DENY`).

## Configuration

The Web host reads the same configuration as the API host (system database, Docker, backups,
`Authentication`, `Security`), plus one section of its own:

```text
Web:
  ApplicationName: Aurora
  SessionHours: 8
```

| Setting | Default | Notes |
| --- | --- | --- |
| `Web:ApplicationName` | `Aurora` | Shown in the header and in page titles. |
| `Web:SessionHours` | `8` | How long a sign-in lasts. 1 to 168. |

Each host has its own `appsettings.json`. The application's settings in the two files are kept
identical, and a test fails if they drift.

## Development

```bash
export Authentication__Jwt__SigningKey="$(openssl rand -base64 48)"
export Authentication__BootstrapAdmin__Username="admin"
export Authentication__BootstrapAdmin__Password="<initial-password>"

dotnet run --project src/AuroraDbManager.Web      # http://localhost:5178
```

Sign in at `/login`. There is nothing to install and nothing to build besides the .NET project:
edit a `.cshtml`, `.css` or `.js` file and reload. `/styleguide` shows the building blocks, and
`/openapi/v1.json` the API, in development only.

UI tests are in `tests/AuroraDbManager.Api.Tests/Ui`. They run the real Web host in-process,
sign in through the real form, submit forms with the antiforgery token of the page they are on,
and read the HTML that comes back. Jobs are run by the test, one at a time, so the states between
*asked for* and *done* are looked at as they are. There are no browser-driven tests.

## Deployment

```bash
dotnet publish src/AuroraDbManager.Web -c Release -o out
dotnet out/AuroraDbManager.Web.dll
```

The output is self-contained as far as the UI goes: compiled pages, and `wwwroot` with the
stylesheet and script (pre-compressed). No Node, no asset pipeline, no separate front-end server.

- **One process (recommended).** Run the Web host. It serves the UI and the API, and runs the
  job worker and the scheduler. Put it behind a TLS-terminating reverse proxy and follow the
  checklist in [security.md](security.md).
- **API only.** Run `AuroraDbManager.Api` instead, where no UI is wanted.
- **Both.** The two hosts can run side by side against the same system database: each is a
  complete node, and jobs and schedules are safe with several (leases, and one claim per
  schedule occurrence). They must then share the Data Protection key ring, which encrypts the
  instance passwords and the session cookies, and each has its own in-memory login rate limit.
  There is rarely a reason to do this.

## Later phases

Each of these builds on the shell, the components and the rules above:

- backups and restores
- backup schedules
- jobs: filtering, paging, detail
- the monitoring dashboard
- user administration
- live updates for long-running work
