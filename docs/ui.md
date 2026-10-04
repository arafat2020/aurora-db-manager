# The UI

Aurora has a server-rendered administration UI, built with ASP.NET Core Razor Pages in
`src/AuroraDbManager.Web`. This document describes its foundation: how it relates to the API, how
users sign in to it, how it is put together, and how to build on it. The pages that manage
instances, databases, backups, schedules and users come in later phases.

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
| Overview, Instances, Databases, Backups, Schedules, Jobs, Monitoring | ✔ | ✔ | ✔ |
| Users | | | ✔ |

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
│   │   ├── _PageHeader.cshtml     title, description, primary action
│   │   ├── _StatusBadge.cshtml    a status: mark, word, colour
│   │   ├── _Alert.cshtml          a message
│   │   ├── _EmptyState.cshtml     what a page shows when it has nothing to show
│   │   ├── _Time.cshtml           an instant, in UTC
│   │   ├── _Icon.cshtml           the icons, inline
│   │   └── _ConfirmDialog.cshtml  the one confirmation dialog
│   ├── Index.cshtml               the overview
│   ├── Login / Logout / Error
│   ├── Instances, Jobs, Users     read-only lists
│   ├── Databases, Backups, Schedules, Monitoring   placeholders
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

`wwwroot/js/aurora.js` is progressive enhancement, about a hundred lines: the menu button on
small screens, closing the account menu, marking a submitted form as busy so it cannot be sent
twice, and the confirmation dialog. It stores nothing, requests nothing, and nothing depends on
it. Later phases may add small things of the same kind (auto-refresh, and SignalR for live job
and instance status); nothing has been prepared for them yet, on purpose.

## Errors

Failures on a page are answered with a page; failures in the API with JSON, as before.

| Situation | What the user sees |
| --- | --- |
| Not signed in | redirect to `/login`, returning to the page afterwards |
| Not allowed (`403`) | *You don't have permission*, at the address that was asked for |
| Not found (`404`) | *Page not found*, with a way back |
| Missing or stale antiforgery token (`400`) | *That request could not be processed* |
| A conflict reported by a service (`409`) | the service's message, on the page that made the request |
| Invalid input | messages at the fields, on the form |
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
sign in through the real form, and read the HTML that comes back. There are no browser-driven
tests yet; they are worth adding once pages have real interactions.

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

- instance management: create, inspect, delete
- database management
- backups and restores
- backup schedules
- jobs: filtering, paging, detail
- the monitoring dashboard
- user administration
- live updates for long-running work
