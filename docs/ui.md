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
| Overview, Instances, Jobs, Monitoring | ✔ | ✔ | ✔ |
| Users | | | ✔ |

What each role may do with instances and databases is in
[Instances and databases](#who-may-do-what), with backups in
[Backups and restores](#backups-and-restores), and with schedules in
[Backup schedules](#backup-schedules).

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
│   │   ├── _BackupTable.cshtml    the backups of a database
│   │   ├── _ConnectionEndpoints.cshtml  where a server is reached, inside and outside Docker
│   │   ├── _RecentJobs.cshtml     the last few operations on an instance or a database
│   │   ├── _Icon.cshtml           the icons, inline
│   │   └── _ConfirmDialog.cshtml  the one confirmation dialog
│   ├── Index.cshtml               the overview
│   ├── Login / Logout / Error
│   ├── Instances/                 list, create, details, delete, external access
│   │   └── Databases/             an instance's databases: list, create, details, delete
│   │       ├── Backups/           a database's backups: list, create, details, restore
│   │       └── Schedule/          a database's backup schedule: view, create/edit, enable/disable, delete
│   ├── Jobs/                      the job history, filtered and paged, and one job
│   ├── Monitoring/                health, scheduler, activity, recent failures
│   ├── Users                      a read-only list
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
- **Tables** are plain HTML tables with `class="table"` inside a `table-wrap`. Give each a
  `<caption>` and `scope="col"` headers, mark the cell that names the row `cell-primary` and the
  cell with its actions `cell-actions`. A cell may wrap onto a second line; a badge and a button
  never break. On a narrow screen every table is shown a row at a time, each value next
  to the name of its column, with nothing left out: the script copies the column names to the
  cells and states the table's roles, and the stylesheet stacks the rows. Without script a table
  stays a table and scrolls inside its frame.
- **Forms** post back to their page. Give every input a `<label>`, hints through
  `aria-describedby`, and errors next to the field with `asp-validation-for`. Validation is the
  server's: the page model's attributes for shape, the application service for everything else.
  Client-side attributes such as `required` are a convenience only. A field the server refused
  is marked `aria-invalid` and described by its message, after its hint; two tag helpers in
  `Components/ValidationAccessibilityTagHelpers.cs` do that for every `asp-for` control, so a
  form needs nothing extra. The primary button comes first and *Cancel* after it; on a page that
  asks before something irreversible, *Cancel* comes first and the button that does it is the
  only solid red one.
- **Words.** One word for one thing: an *instance* is what Aurora manages, its *database server*
  is the process in it, *this host* is the machine Docker runs on, and *Aurora* is the
  application and its configuration. Actions are *Create …*, *Edit …*, *Delete …*; going to a
  row's page is *Open*. Every instant is written `2026-10-05 18:00 UTC`.
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
| `/instances/{id}` | One instance: overview, health, connection, credential, its databases, recent operations | `InstanceService.GetAsync`, `InstanceHealthService.GetAsync`, `InstanceConnectivityService.GetAsync`, `CredentialRotationService.GetAsync`, `DatabaseService.ListAsync`, `JobService.ListAsync` |
| `/instances/{id}/delete` | The question, and the form that deletes | `InstanceService.DeleteAsync` |
| `/instances/{id}/external-access` | The question, and the forms that enable and disable external access | `InstanceConnectivityService.EnableAsync`, `DisableAsync` |
| `/instances/{id}/rotate-password` | The question, and the form that starts a rotation of the administrator password | `CredentialRotationService.RequestAsync` |
| `/instances/{id}/rotate-password/{jobId}/result` | The new password of a completed rotation, once, in the answer to a `POST` | `CredentialRotationService.RetrieveResultAsync` |
| `/instances/{id}/databases` | The instance's databases, 20 to a page | `DatabaseService.ListAsync` |
| `/instances/{id}/databases/create` | The form that creates one | `DatabaseService.CreateAsync` |
| `/instances/{id}/databases/{databaseId}` | One database: overview, connection, recent operations | `DatabaseService.GetAsync`, `InstanceConnectivityService.GetForDatabaseAsync`, `JobService.ListAsync` |
| `/instances/{id}/databases/{databaseId}/delete` | The question, and the form that deletes | `DatabaseService.DeleteAsync` |

Ids are GUIDs; anything else in their place is no page. A database is always addressed under its
instance, and a database of another instance is not found there. Its backups are addressed under
it in turn ([Backups and restores](#backups-and-restores)). There is no top-level
*Databases* section: nothing lists databases across instances, in the application or in the API,
so they are reached through *Instances*.

### Who may do what

| | viewer | operator | admin |
| --- | :---: | :---: | :---: |
| See instances, their details, health and databases | ✔ | ✔ | ✔ |
| See the managed credential: its username and when it was last rotated | ✔ | ✔ | ✔ |
| Create and delete a database | | ✔ | ✔ |
| Rotate the administrator password, and see the new one once | | ✔ | ✔ |
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
  what that means (*this host only*, *one network interface*, *every network interface*), the
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

With one exception, no page shows, embeds or logs a credential. The administrator password of
an instance stays in the secret store, as it does for the API; the pages never ask for it. The
exception is the page that shows the new password of a completed rotation, once
([The new password, once](#the-new-password-once)). Connection strings on the
pages are templates with `<password>` in place of the password, and the only hidden fields of
any form are the antiforgery token and, on the jobs list, the id it is narrowed to.

### The managed credential

Every instance has one credential that Aurora manages: the engine's administrator account,
`postgres` for PostgreSQL and `root` for MySQL, with a password Aurora generated when the
instance was created. Aurora itself connects with it to create and delete databases, to back
them up and to restore them. An instance page has a *Credential* section that says so:

- **Credential**: *Managed by Aurora*, or *Not yet* while the instance is being provisioned.
- **Username**: the account.
- **Password**: *Not displayed*. It cannot be chosen, and this page never shows it. It is shown
  once, right after a rotation, from that rotation's job.
- **Last rotated**: when, or *Never*.
- **Last rotation**: the status of the most recent rotation and a link to its job.

### Rotating the password

Operators and administrators see *Rotate password* in that section while the instance is
running and no rotation is under way. It leads to `/instances/{id}/rotate-password`, a page
that asks before anything happens and says what will:

- Aurora generates a new password; nobody types one, and the form has no field for one;
- **the current password stops working** once the rotation has succeeded, so anything outside
  Aurora that connects as the administrator with it can no longer connect;
- **the new password is shown once**, on request, after the rotation has succeeded, to an
  operator or an administrator, for a limited time; Aurora stores it encrypted and uses it;
- connections that are open stay open, the database server is not restarted, and no data is touched.

Only its form changes anything: a `POST` with an antiforgery token. It starts a
`rotate_credential` job and leads to that job's page, which says *The password is being
rotated*, reloads while the job runs, and ends with *The password was rotated* or, if the job
failed, *The password was not rotated* with the job's stable error code and message.

If the service refuses (the instance is not running, a rotation is already under way, or one of
the instance's databases is being created, deleted, backed up or restored) the page comes back
with the code and sentence the API uses and status `409`, and nothing was started. The reverse
holds too: while a rotation is unfinished, creating and deleting databases, backups, restores,
changing external access and deleting the instance are refused with
`CREDENTIAL_ROTATION_IN_PROGRESS`. A scheduled backup that falls due in those seconds is
skipped, like one that finds its database busy.

### The new password, once

A password that nobody outside Aurora can learn is of no use to a client outside Aurora, so a
completed rotation hands its new password out: **once**.

- The job page of a completed rotation has a *New password* section. To an operator or an
  administrator it offers *Show the new password*; a viewer is told that it is available and is
  offered nothing. The job page itself never contains the password.
- The button sends a `POST` with an antiforgery token to
  `/instances/{id}/rotate-password/{jobId}/result`. The answer is a page with the username, the
  password, and *Copy password*, under the heading *This password will only be shown once*. It
  is sent with `Cache-Control: no-store`. The password is in the page's text and nowhere else:
  not in the address, a redirect, a cookie, a field or a script, and nothing is put in the
  browser's storage. *Copy password* copies when it is pressed and never by itself.
- That was the one time. Reloading the page sends the form again and is answered *Credential
  already retrieved. It cannot be displayed again.* with status `409`; opening the address leads
  back to the job, which says the same and no longer offers the button.
- **Any** operator or administrator can ask, not only the one who started the rotation, so a
  colleague can pick it up; but whoever asks first is the only one who sees it.
- It can be asked for during 15 minutes after the rotation completed
  (`Credentials:ResultTtlMinutes`). After that the job page says the time has passed. The
  password itself keeps working for Aurora; to get one that can be used, rotate again.
- Asking for another rotation withdraws an older password that nobody retrieved: it is about to
  stop working.
- A rotation that failed has nothing to show.

If something goes wrong inside Aurora while the password is being fetched, the request fails and
the one time is **not** used up: recording that it was shown and reading it are one transaction.
What cannot be undone is a page that was sent and never arrived, or was closed unread; then the
password is rotated again.

**When a rotation fails part-way.** The database server and Aurora's secret store cannot be
changed in one step, so Aurora stores the new password, encrypted, *before* it tells the server
about it, and keeps it until the server is known to accept it and it has become the password
Aurora uses. A job that is interrupted or fails therefore never loses track of what the server
may have been changed to. The instance page then says *The last password rotation did not
finish*; rotating again finishes that rotation with the same password instead of starting
another. Until then Aurora's own work in that instance may fail to connect, which is the reason
to do it. The one state Aurora cannot repair is a server that accepts neither password, which
means its administrator password was changed by someone else: the job fails with
`CREDENTIAL_ROTATION_RECOVERY_REQUIRED` and changes nothing.

**What does not change.** The container, its environment and its data volume are not touched.
The password variable in the container's environment (`POSTGRES_PASSWORD`, `MYSQL_ROOT_PASSWORD`)
is what the engine read once, when it initialized its data directory; after a rotation it holds
a password that no longer works, and that is expected. Restarting the server or Aurora, or
reconciling, never puts an old password back.

## Backups and restores

A database's backups are reached from the database: its page has a *Backups* section with the
newest few, and the list of an instance's databases has a *Backups* link on each row. There is no
top-level *Backups* section; nothing lists backups across databases, in the application or in the
API.

These pages are a presentation of `BackupService` and `RestoreService`, the services behind
`/api/v1/databases/{id}/backups` and `/api/v1/backups/{id}`, and of the jobs `JobService` lists.
No page makes, reads, checks or restores a backup, touches the backup storage, or runs anything
against a database. Nothing in the backend was changed for them.

```text
Razor Page  ──►  BackupService / RestoreService  ──►  job system  ──►  dump tools · backup storage · database
```

### Pages

| Address | Page | Reads and calls |
| --- | --- | --- |
| `…/databases/{databaseId}/backups` | The database's backups, 20 to a page (`?page=2`) | `BackupService.ListAsync`, `JobService.ListAsync` |
| `…/backups/create` | What will be backed up and where to, and the form that asks for it | `BackupService.CreateAsync` |
| `…/backups/{backupId}` | One backup: status, size, storage, integrity, checksum, operations on it | `BackupService.GetAsync`, `IBackupStorageResolver.Resolve`, `JobService.ListAsync` |
| `…/backups/{backupId}/restore` | The question, and the form that asks for the restore | `RestoreService.CreateAsync` |

(`…` is `/instances/{id}`.) Every page says which database of which instance it is about, in its
header and its breadcrumbs: *Instances / production / Databases / shop / Backups*. A backup is
only found under its own database; under any other address it is *Backup not found*.

### Who may do what

| | viewer | operator | admin |
| --- | :---: | :---: | :---: |
| See backups, their status, size, storage and integrity | ✔ | ✔ | ✔ |
| Create a backup | | ✔ | ✔ |
| Restore a backup | | ✔ | ✔ |

The create and restore pages carry the operator policy of the API endpoints that do the same. A
viewer who opens one, or sends its form by hand, gets the `403` page.

### The list

Newest first: when the backup was asked for, its status (**Pending**, **Running**, **Completed**,
**Failed**, with the stable error code of a failed one), its size, the storage it is in, and its
integrity. Paging is the service's (`?page=2`, 20 to a page); `?page=0` and a page size above the
service's maximum are refused with the `400` page, and a page past the last one says so with a
way back. A database without backups has an empty state that offers *Create backup* to those who
may. A history that holds only failed backups says there is nothing to restore from yet.

### Creating a backup

The create page shows the database, the instance, the engine, and the **storage the backup will
go to**. That storage is the server's setting (`Backups:StorageType`), exactly as for the API: a
request for a backup is the database and nothing else, so the page has no selector and no other
field. It shows the storage's type (*Local* or *S3*) and never a directory, bucket, key or
credential.

Submitting asks `BackupService` for the backup. It is made by a job, so the page it leads to says
*Backup creation started.* and shows the backup as **Pending**, then **Running**. It is
**Completed** only when the job has stored the artifact, read it back from the storage and
matched it against its checksum. If the service refuses (the database is not ready, the instance
not running, a backup or a restore already under way) the page comes back with the stable code
and sentence and status `409`, and nothing was created.

### Storage

A backup is in the storage its record names, which is the storage that was the server's default
when it was made and need not be the default now. The pages show the backup's own storage, never
the current setting: after a change from S3 to local, an older backup still says *S3*, and is
restored from S3.

If the server no longer has settings for a backup's storage, the backup's page says *Not
configured on this server*, does not offer a restore, and a restore that is asked for anyway is
refused with `BACKUP_STORAGE_NOT_CONFIGURED`. Whether a storage is usable is asked of
`IBackupStorageResolver`, the resolver every restore goes through; the pages construct no path
and no key.

### Integrity

Integrity is what the backup's record says. Nothing is hashed or compared by a page.

| Shown | Means |
| --- | --- |
| **Verified** | The backup is completed and has a checksum. The job read the stored artifact back and matched it before completing the backup. |
| **No checksum** | The backup was completed before checksums were recorded. It is a backup; a restore checks it by size and format only. |
| *Not available* | The backup is not finished, or failed: there is no artifact. |

The checksum itself (SHA-256) is on the backup's page, folded away under its name. A backup is
checked again when it is restored: one that no longer matches its checksum is not restored, the
restore fails with `RESTORE_ARTIFACT_CHECKSUM_MISMATCH` before anything in the database is
changed, and the backup's page shows that failure. A backup that failed its check while it was
being made is a **Failed** backup with `BACKUP_CHECKSUM_MISMATCH`.

### Restoring

Restoring replaces everything in a database, so it takes more than a click.

- *Restore* on a completed backup is a link to the restore page. It restores nothing.
- The page names the database and its instance, the backup's time and ID, its size, storage and
  integrity, and then says what will happen: the current contents are removed and replaced; what
  was created or changed since the backup is lost; this cannot be undone through Aurora unless
  another backup holds the current data; clients are disconnected and the database may be
  unavailable meanwhile.
- Only the page's form restores: a `POST` with an antiforgery token. A `GET` restores nothing,
  whatever its address carries, and there is no script confirmation standing in for the page.
- The target is always the backup's own database. The form has no field to name another.
- Whether the restore is accepted is `RestoreService`'s to say: a completed backup, a usable
  storage, a ready database, a running instance, and nothing else at work in the database. The
  page checks none of that itself, and shows a refusal with its stable code and status `409`.

A restore is a job. The page it leads to, the backup's, says *Restore started.* and shows
**Restore in progress**; the database's backup list says the same. When the job is done the
backup's page says **Restore completed** with the time, or **The last restore failed** with the
job's stable code and message, and that a failed restore may have left the database empty or
partly restored, which restoring again repairs. No progress percentage is shown, because the
application has none.

### Work in progress

As on the instance and database pages: while a backup is pending or running, or a restore is
under way, the page shows *Work is in progress* with a **Refresh** link and, with JavaScript,
reloads itself every five seconds while it is visible, at most 60 times. No SignalR, no polling
of an endpoint.

### Not included

- **Deleting a backup.** The application has no such operation, so there is no button for it.
- **Restoring into another database**, downloading a backup, or uploading one.
- **Choosing the storage** of a new backup. It is the server's setting.
- A cross-database backup overview. Schedules are in [Backup schedules](#backup-schedules).
- *Operations on this backup* lists what is among the database's 50 most recent jobs. Older ones
  are in *Jobs*.

## Backup schedules

A database can have one backup schedule, and its page is the database's:
`/instances/{id}/databases/{databaseId}/schedule`. It is reached from the *Backups* section of
the database's page, which also shows the schedule in a line. There is no top-level *Schedules*
section and no list of schedules: a database has a schedule or it has none.

The pages are a presentation of `BackupScheduleService`, the service behind
`/api/v1/databases/{id}/backup-schedule`. Its calculator reads cron expressions and time zones
and says when a schedule next runs; the scheduler acts on that. The pages parse nothing,
calculate nothing and start nothing.

### Pages

| Address | Page | Reads and calls |
| --- | --- | --- |
| `…/schedule` | The schedule, or that there is none | `BackupScheduleService.GetAsync` |
| `…/schedule/edit` | The form that creates the schedule or changes it; and the enable and disable actions | `CreateAsync`, `UpdateAsync` |
| `…/schedule/delete` | The question, and the form that deletes | `DeleteAsync` |

(`…` is `/instances/{id}/databases/{databaseId}`.)

### Who may do what

| | viewer | operator | admin |
| --- | :---: | :---: | :---: |
| See the schedule | ✔ | ✔ | ✔ |
| Create, edit, enable, disable, delete | | ✔ | ✔ |

The edit and delete pages carry the operator policy of the API's endpoints, for their forms and
for the enable and disable actions alike.

### What the schedule page shows

The database and its instance; whether the schedule is **Enabled** or **Disabled**; the cron
expression; the time zone; the next run; when it was created and last updated; its ID.

- **Next run** is the `nextRunAt` on the schedule's record, which the service calculated. It is
  shown as the instant, in UTC, and next to it as the clock of the schedule's time zone shows it:
  *2026-10-05 18:00 UTC, which is 2026-10-06 00:00 in Asia/Dhaka*. The second is the same instant
  written differently, not a calculation of when the schedule runs.
- A **disabled** schedule has no next run, and the page says *Disabled* there.
- *Last updated* is also when the schedule last moved on to its next run, because that is what
  the record holds.
- **There is no "last scheduled backup".** A backup the schedule causes is an ordinary backup
  with an ordinary job; nothing on record tells it from one asked for by hand. The page links to
  the database's backups and jobs instead of guessing.

### Creating and editing

One form, with the three fields the service's request has:

- **Cron expression.** Five fields: minute, hour, day of month, month, day of week, with examples
  next to the field. The page does not check it. `BackupScheduleService` does, and what it
  refuses comes back at the field with the API's sentence (`422`), with what was typed kept.
  Nothing in the browser parses cron.
- **Time zone.** The IANA name of the zone the expression is read in, typed into a text field
  that suggests the names the server knows and the calculator accepts. It is never taken from
  the browser or from the server's own zone; what is written is what the schedule runs by.
  Anything the calculator does not accept is refused at the field.
- **Enabled.** On for a new schedule.

Creating leads to the schedule's page with *Backup schedule created.*; editing, with *Backup
schedule updated.* and the next run worked out anew by the service. The edit form starts from
what the schedule says. Saving a schedule backs nothing up, and no page says it did: the first
run is the next occurrence after now. A schedule is only given to a database that could be
backed up now; otherwise the service refuses with `DATABASE_NOT_READY` or `INSTANCE_NOT_READY`
(`409`).

### Enabling and disabling

The service has no operation that only enables or disables; a request states the whole
schedule. The *Enable schedule* and *Disable schedule* buttons therefore send the schedule back
as it is, with that one value changed, through `UpdateAsync`. Disabling keeps the schedule and
leaves it without a next run. Enabling starts it from the next occurrence after that moment; an
occurrence that passed while it was disabled is not made up for. The page never writes a next
run itself.

### Deleting

*Delete schedule* leads to a page that names the database, shows the schedule, and says what
follows: automatic backups of the database stop, existing backups are kept, a backup that is
running is not stopped, and that disabling pauses a schedule without removing it. Only its form,
a `POST` with an antiforgery token, deletes.

## Jobs

`/jobs` is the job history, as `JobService` lists it for `/api/v1/jobs`: one page at a time,
newest first.

- **Columns:** the operation (a link to the job), its status with the stable error code of a
  failed job, the attempt, when it was created, started and finished, and what it worked on, as
  links to the instance, the database and the backup.
- **Filters:** status and operation, as two selects, and instance and database through the
  address (`?instanceId=…`, `?databaseId=…`), which is how the *All jobs of this instance* and
  *All jobs of this database* links on those pages arrive. They are the filters the service has,
  under the names the API takes, in any combination. The form is a `GET`: the address is the
  query, and paging keeps it. Nothing is filtered in the browser, and nothing is loaded that is
  not shown.
- **Paging** is the service's: 20 to a page, `?page=2`. A status or operation the application
  does not have, an id that is not one, and a page that is not a page are refused with the `400`
  page, as the API refuses them.
- No jobs at all, no jobs matching the filters, and a page past the last one each say so.

`/jobs/{id}` is one job: the operation, status, attempt, times, the instance and database by
name with links (or *No longer exists*), the backup it made or restored, and its ID. A failed
job shows the stable **error code** and the **message** the application recorded for clients,
and nothing else: no exception, no output of a tool, no path. A job that is pending or running
has the *Work is in progress* line and reloads like the other pages.

Jobs cannot be started, retried or cancelled from here; the application has no such operations.

## Monitoring

`/monitoring` is the state of the installation in more detail than the overview. It is read
from what monitoring already provides and from nothing else: `MonitoringSummaryService` (the
summary behind `/api/v1/monitoring/summary`) and the application's own `HealthCheckService`
(the checks behind `/health/ready` and `/health/storage`). No page pings Docker, connects to a
database, or counts rows.

| Section | Shows | From |
| --- | --- | --- |
| Health | *Aurora* (readiness), *System database*, *Docker*, *S3 backup storage* | the health checks |
| Backup scheduler | whether any schedule is overdue, enabled schedules, the earliest next run, the last pass | the summary's scheduler part |
| Activity | instances by status; jobs, backups and restores pending, running and failed recently | the summary |
| Recent failures | the latest failed jobs with their stable codes, linked to the job and to what it worked on | the summary |

- **Health** uses the checks' own statuses: *Healthy*, *Degraded*, *Unhealthy*. Docker being
  unreachable is *Degraded*, as readiness has it. A check that does not apply to this server (S3
  when backups are local) says *Not in use*. Why a check failed is in the server's log, not on
  the page. Opening the page runs the S3 check, which is one request to the object store.
- **The scheduler** has no health status of its own in the application, and the page does not
  make one up. It shows what the summary has: *On time* or *N overdue* (the summary's own notion
  of overdue), and the last pass, which is kept in memory and so is *None since this process
  started* until the scheduler of this process has made one.
- **Instance health** is not on this page: finding it out means asking each instance's server.
  It is on each instance's page, and each database's page shows the health of its instance, said
  to be that. The application has no health check of a single database.
- The page is as of the moment it was rendered, with a **Refresh** link. It does not reload
  itself.

The overview (`/`) is unchanged but for its recent failures now linking to their jobs.

## Styling

One hand-written stylesheet, `wwwroot/css/aurora.css`, with design tokens as CSS custom
properties and a small set of component classes. Tailwind was considered and not used: it needs a
build step (Node or a platform-specific binary) for something a few hundred lines of CSS do
here, and a UI with no build step is one less thing to break. There is no component library and
no web font; the UI uses the system font.

- Light and dark follow the operating system (`prefers-color-scheme`).
- Desktop first, and usable down to a 390 px phone. Below about 830 px the sidebar becomes a
  panel behind a menu button, tables are shown a row at a time, definition lists put each term
  above its value, and filters and cards stack. No page scrolls sideways at any width.
- Colours are tokens, defined once for light and once for dark; no rule names a colour itself,
  and a test fails if the dark theme lacks a colour the light one has. Text meets a 4.5:1
  contrast ratio in both.
- No inline styles and no inline scripts, anywhere: the content security policy forbids them, and
  a test checks every page.
- Animation is one spinner, and it stops for users who prefer reduced motion.

### JavaScript

`wwwroot/js/aurora.js` is progressive enhancement, well under two hundred lines: the menu button
on small screens, closing the account menu, marking a submitted form as busy so it cannot be sent
twice, the confirmation dialog, reloading a page that shows work in progress (see
[Work that takes a while](#work-that-takes-a-while)), the *Copy* buttons of the Connection
sections, which write text that is already on the page to the clipboard, and naming the columns
at the cells of tables so that they can be stacked on narrow screens. It stores nothing, makes no request of its
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

- user administration
- live updates for long-running work
