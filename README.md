# DoiQ

DoiQ is a personal kanban todo board shared between a person (the user) and AI agents such as Hermes. Both sides see the same board: the user works in the web UI, agents use a REST API or an MCP endpoint, and changes appear live through SignalR. It is a single .NET 10 container (ASP.NET Core serving a Blazor WebAssembly client, REST API, SignalR hub and MCP at `/mcp`) on top of one SQLite file on a mounted volume.

## Run with Docker

```bash
docker compose up -d --build
```

The UI, API, hub and MCP all answer on <http://localhost:8080>. The only things compose configures are the port mapping and `./data:/data`; the database lives at `./data/doiq.db`. There are no keys or passwords in the Dockerfile or compose file.

On Linux the container runs as uid/gid `1654` (user `app` in the .NET image), so give it the data folder once:

```bash
mkdir -p data && sudo chown 1654:1654 data
```

The login cookie is signed with ASP.NET Data Protection keys, stored in a `keys` folder next to the database (`./data/keys`, override with `DataProtection__KeysPath`). They are **not encrypted at rest** (there is no certificate in this setup), so protect the data folder like the database itself. Keep `keys` together with `doiq.db` and include it in backups: if it is lost, everyone is simply logged out once.

Health check: `GET /health` returns `{"status":"ok"}` (also used by the image's `HEALTHCHECK`).

### Published images

Prebuilt images for `linux/amd64` and `linux/arm64` are on GitHub Container Registry, so a host doesn't need the source code:

| Tag | What it is |
|---|---|
| `latest`, `1.2.3`, `1.2` | The newest release (a `v1.2.3` git tag) |
| `beta` | The newest build: every push to `main`, any pre-release tag such as `v1.3.0-beta.1`, or a manual run of the workflow. Always at least as new as `latest`. |
| `1.3.0-beta.1` | That pre-release |
| `sha-<commit>` | One specific build of `main` |

A compose file that pulls them instead of building (swap `latest` for `beta` to follow the newest build):

```yaml
services:
  doiq:
    image: ghcr.io/thindaltv/doiq:latest
    ports:
      - "8080:8080"
    volumes:
      - doiq-data:/data
    restart: unless-stopped

volumes:
  doiq-data:
```

A named volume like `doiq-data` takes its owner from the image, so no `chown` is needed; use a host path such as `./data:/data` instead if you want the files in a folder you can browse (then `chown 1654:1654` it once, as above). Update with `docker compose pull && docker compose up -d`.

The images are built by `.github/workflows/docker.yml` after the tests pass. To publish a beta from any branch, run the workflow from the repository's Actions tab ("Run workflow"). To release, push a version tag, e.g. `git tag v0.1.0 && git push origin v0.1.0`.

## First-run setup

Open the site in a browser. While no password exists the app shows first-run setup (`GET /api/setup/status` returns `{"needsSetup":true}`); choose the user's password there. Everything else is configured in the app and stored in the database, so it survives rebuilds as long as `./data` does.

Put DoiQ behind HTTPS (reverse proxy, tunnel) before exposing it beyond your LAN: the login cookie and agent keys travel over the wire. Plain HTTP on the LAN (port 8080) keeps working.

Login and first-run setup are rate limited per client IP: 5 attempts per 60 seconds, then `429 Too Many Requests` with a `Retry-After` header. Tune it with `Auth__RateLimit__PermitLimit`, `Auth__RateLimit__WindowSeconds` and `Auth__RateLimit__MaxConcurrentVerifications` (password checks running at once, default 2). All optional.

### Behind a reverse proxy

The proxy terminates TLS and talks plain HTTP to DoiQ. It must send `X-Forwarded-Proto: https` (so the login cookie is issued with the `Secure` flag) and `X-Forwarded-For` (so the rate limit sees the real client, not the proxy). Nginx, Caddy and Traefik can do this; for nginx:

```nginx
proxy_set_header X-Forwarded-Proto $scheme;
proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
proxy_set_header Host              $host;
# SignalR (live updates) needs WebSocket upgrade support:
proxy_http_version 1.1;
proxy_set_header Upgrade    $http_upgrade;
proxy_set_header Connection "upgrade";
```

DoiQ only believes those headers from proxies you name, otherwise anyone could forge them. By default only a proxy on the same machine (loopback) is trusted. If the proxy reaches DoiQ over a Docker network or another host, list it:

```
Proxy__KnownProxies__0=10.0.0.5          # a single proxy address
Proxy__KnownNetworks__0=172.16.0.0/12    # or a whole network (CIDR), e.g. the default Docker bridge range
```

Without a trusted proxy the headers are ignored: the cookie then has no `Secure` flag and every client counts as the proxy's IP for rate limiting. Check with a login: the `Set-Cookie: doiq.auth=...` header should contain `secure`.

## Adding agents

Go to **Settings** in the UI and add an agent (name, initials, whether it is the triage agent or the dispatcher, and its responsibilities, which it can fill in itself; see "Handing work to an agent" below). The app creates an API key and shows it to you; copy it into the agent's configuration. Keys can be rotated from the same screen. Webhook URL and secret for each agent are also set there. Settings is the only place agents are managed by hand; nothing about them is read from configuration.

**Letting an agent manage agents (orchestrators):** tick **Orchestrator** on an agent in Settings and it may register, change and delete other agents itself, with the MCP tools `register_agent`, `configure_agent` and `delete_agent` (REST `POST /api/orchestrator/agents`, `PATCH /api/orchestrator/agents/{name}`, `DELETE /api/orchestrator/agents/{name}`). Registering returns the new agent's API key and, unless one was given, a generated webhook secret, once, for the orchestrator to hand to the new agent. An orchestrator sets name, initials, webhook URL and secret, notify URL and responsibilities, and can rotate a key; it cannot give out the triage, dispatcher or orchestrator roles, and cannot change or delete itself or another orchestrator. Only the user can do those things.

**Deleting an agent** (Settings, or `delete_agent`) is refused while open tasks are still assigned to it. An agent that never took part in a task is removed. One that did stays in those tasks' history as "Name (deleted)", with no key, webhooks or roles, and its name is free for a new agent.

**Forgot the password?** Stop the app and delete the stored hash; the next start shows first-run setup again (existing data is kept):

```bash
docker compose stop doiq
sqlite3 data/doiq.db "DELETE FROM Settings WHERE Key = 'password-hash';"
docker compose start doiq
```

## Boards

DoiQ can hold several boards, each with a name. A fresh database (and an upgraded one, whose existing tasks all move onto it) starts with one board called **Main**. A task is on one board at a time and can be moved to another (a lane move next to a task from another board is still rejected).

- **Moving a task to another board:** in the drawer, pick the board under **Board** (shown when there is more than one). The task keeps its lane, assignee, fields, comments, checklist and history, and goes to the bottom of its lane on the new board; it leaves the old board live in every open tab, and the notice links to where it went. Agents use MCP `move_task_to_board` (`boardId` or `boardName`) on any task they may edit; REST is `POST /api/tasks/{id}/move-to-board` with `{ "boardId": 2 }` or `{ "boardName": "Home" }` and an optional `expectedVersion`. It records a `MovedToBoard` event on the new board (payload `from`, `to`), wakes the task's agent like other changes by the user, and the task's queued webhooks follow it. A task made from a source (`sourceRef`) cannot go to a board that already has a task from the same source (409).

- **Switching:** the board menu in the top bar (key `B`; `Shift H` / `Shift L` for previous/next board). Each board has its own URL (`/board/<id>`), the last board used is remembered per browser, and the board name is in the page title.
- **Managing:** **Settings > Boards** (the user only; agents cannot add, rename or delete boards). Names are required, trimmed, 1-60 characters and unique ignoring case. Each board shows how many tasks are open and done.
- **Deleting:** the button explains and stays disabled while the board holds any task that is not in Done, and for the last remaining board. Otherwise a confirm dialog names the board and its task count; focus starts on Cancel. The server enforces the same rules: `DELETE /api/boards/{id}?confirm=true` (a bare DELETE does nothing, 409 with the count while tasks are not done). Deleting removes the board's tasks with their comments, fields, subtasks, activity, read markers and queued webhooks.
- **API:** `GET /api/boards`; task lists, creating tasks and `/api/events` take a `boardId`. With only one board it is optional; with several it must be given (the error lists the boards). Live updates carry the board id and the client applies only the board it shows.
- **Agents:** one inbox spans all boards and every item carries `boardId` and `boardName`. The triage agent triages New tasks on every board. MCP `list_boards` lists them; `create_task`, `search_tasks` and `get_changes` accept `boardId` or `boardName` (with several boards `create_task` and `search_tasks` return an error listing them when neither is given). Webhook payloads include `boardId` and `boardName`.

## Archive

Nobody archives by hand. A task that has been in **Done** for longer than `Board:ArchiveAfterDays` (default 14, env `Board__ArchiveAfterDays`) is archived: it leaves the board, keeps all its fields, comments and history, and stays reachable. Moving it out of Done (from the drawer, any client or an agent) clears its done date and puts it back on the board, live in every open tab.

- **Board:** the board loads only its active tasks. The Done lane's footer says how many older tasks are hidden and links to the archive view. Cards show their counts; the drawer loads a task's description, fields, comments and checklist when it opens.
- **Archive view:** `/board/<id>/archive` (top bar **Archive**, key `Shift A`) lists the board's archived tasks, most recently done first, 50 at a time with **Load more**, with title, done date, assignee and field/comment counts. Its search box searches titles, descriptions, field labels and values, comments and checklist lines. Opening a line shows the usual drawer; set its status to anything but Done to reopen it.
- **Search on the board:** the top-bar search also covers the archive: archived matches appear in an **Archived** group below the lanes (the newest 10, with a link to all of them in the archive view).
- **API:** `GET /api/boards/{id}/tasks` is the board load (active tasks with their counts, `archivedCount`, `latestEventId` read before the tasks, at most `Board:MaxBoardTasks` tasks, default 2000, `truncated` says when more were left out). `GET /api/boards/{id}/archive?q=&cursor=&limit=` pages the archive (default 50, at most 200) with an opaque keyset cursor on done date and id, so pages stay stable while tasks are archived or reopened; `GET /api/boards/{id}/search?q=&archivedLimit=` returns the matching active task ids plus the first archived matches. Every task summary has `archived` (true/false). The archive queries use the `(BoardId, Status, DoneAt)` index.
- **Agents:** `get_inbox` never includes archived tasks. `search_tasks` leaves them out unless `includeArchived` and is paged: it returns `{tasks, nextCursor}` (`limit` 1-200, default 50); pass `cursor=nextCursor` with the same arguments for the next page until `nextCursor` is null. The cursor is a keyset position in the requested sort, so a task added meanwhile does not shift the next page; a cursor used with another `sort` or `dir` is refused.

## MCP and API authentication

- MCP endpoint (streamable HTTP, stateless): `http://<host>:8080/mcp`
- REST API: `http://<host>:8080/api/...`
- Health check for an agent's MCP settings: `GET /mcp` with the agent's key answers `200 {"status":"ok","actor":"<agent name>",...}`; a missing or wrong key gets `401`. (MCP calls themselves are POSTs to the same URL.)

An agent identifies itself on every request with its key, in either header:

```
X-Api-Key: <key>
Authorization: Bearer <key>
```

## Connecting Hermes (webhooks and MCP)

Hermes has no SignalR client, so DoiQ pushes to it: a change the user makes that an agent should react to is queued in an outbox table and POSTed to the agent's webhook URL. The agent then reads the details over MCP. Everything below is configured per agent in **Settings** (webhook URL, secret, notify URL, API key); nothing goes into container environment variables.

### 1. Webhook routes on the Hermes side

Run `hermes gateway setup` once, then subscribe two routes (the adapter listens on port 8644 by default; check the exact flags with `hermes webhook subscribe --help`, they are not verified here):

```bash
# wake-up: Hermes reads its inbox over MCP when this fires (give it a prompt template that says so)
hermes webhook subscribe doiq

# notify the user: deliver-only, no LLM call, the "message" field is sent to the user's chat
hermes webhook subscribe doiq-notify --deliver-only
```

In DoiQ Settings, edit the agent: **Webhook URL** `http://<hermes-host>:8644/webhooks/doiq`, **Notify URL** `http://<hermes-host>:8644/webhooks/doiq-notify`, and put the secret each route reports (or one you chose) into **Webhook secret**. DoiQ signs both routes with that one secret.

### 2. What DoiQ sends

| Route | Body | Fires when |
| --- | --- | --- |
| `doiq` | `{ "eventId", "boardId", "boardName", "taskId", "type", "actor", "title", "at", "reason" }` (`reason` is `"Mentioned"` when the agent was @mentioned, otherwise null) | The user creates, edits, moves, comments on, adds a field to, or assigns a task that is the agent's to look at; another agent assigns it a task; anyone @mentions it |
| `doiq-notify` | `{ "eventId", "boardId", "boardName", "taskId", "message" }` with `message` = `"<agent> assigned you: <title>"` (or `"<agent> assigned you a subtask: <subtask> (on <title>)"`, `"<agent> needs your input: <title>"` from `ask_user`, or `"<agent> mentioned you: <title>"`) | The agent creates a task for, or assigns a task or subtask to, the user, asks the user something, or @mentions them |

Who gets a `doiq` webhook: the assignee if it is an agent; for a pool (`any`) task the dispatcher if there is one, otherwise every active agent (they race on `claim_task`); only the triage agent for a task in New; agents a subtask is assigned to; an agent another agent assigns a task to (the dispatcher or triage agent passing work on); every agent @mentioned in a comment. Deactivated agents get nothing, and an agent's other changes wake nobody. Changes to the same task and target within the debounce window (30 s, `Board__DebounceSeconds`) go out as one webhook carrying the latest event, so "create, enrich, assign, move" is one interruption. A webhook is a wake-up, not proof of work: Hermes should call `get_inbox`; a repeat wake finds nothing past `mark_handled` and does nothing.

Each request carries:

- `X-Webhook-Timestamp`: unix seconds
- `X-Webhook-Signature-V2`: lowercase hex HMAC-SHA256 of `"<timestamp>.<raw body>"` (the raw bytes as sent) keyed with the agent's webhook secret. Without a secret DoiQ sends no signature header.

Failed deliveries (non-2xx, timeout, connection refused) are retried with exponential backoff, 10 s doubling up to 1 h, 10 attempts, then the row is closed with its error kept. Because the outbox is a database table, a restart of DoiQ loses nothing. Tuning (mostly for tests): `Outbox__RetryBaseSeconds`, `Outbox__RetryMaxSeconds`, `Outbox__MaxAttempts`, `Outbox__PollSeconds`.

### 3. Safety net: a 15-minute inbox sweep

Webhooks can be dropped (Hermes down for longer than the retries, a URL typo). Add a Hermes cron job that runs every 15 minutes with the prompt "Call `get_inbox` on DoiQ and work through what is there; call `mark_handled` for each task when done." A dropped webhook then only delays work. It also picks up unclaimed pool tasks and expired claims.

### 4. MCP server in the Hermes config

Add DoiQ as a streamable-HTTP MCP server with the agent's key (shown once when you create or rotate it in Settings). The key decides who the agent *is*: every tool acts as the agent that owns the key, there is no actor argument. Sketch of the Hermes config (check the key names against your Hermes version):

```yaml
mcp_servers:
  doiq:
    url: http://<doiq-host>:8080/mcp
    headers:
      X-Api-Key: <the agent's key>      # or: Authorization: Bearer <key>
```

The 25 tools: `list_boards`, `list_actors`, `get_inbox`, `get_changes`, `search_tasks`, `get_task`, `create_task`, `update_task`, `move_task`, `move_task_to_board`, `assign_task`, `ask_user`, `upsert_field`, `add_comment`, `mark_seen`, `mark_handled`, `add_subtask`, `update_subtask`, `claim_task`, `renew_claim`, `release_task`, `set_my_responsibilities`, and for orchestrators `register_agent`, `configure_agent`, `delete_agent`. Their descriptions are the agent's instructions (for example `move_task` says which tasks the agent may move). Rule violations come back as tool errors with text the model can act on ("Another agent holds this task. You can only comment on it.", "Task is already claimed by another agent").

Handing work to an agent:

- **Giving it away:** when the user assigns a task in **New** to an agent (or to "Any agent"), or creates one assigned that way, the task moves to **Waiting** and belongs to the agents from then on. A task already past New keeps its lane. An agent assigning a New task (the triage agent passing work on) leaves it in New.
- **Working it:** the agent moves the task to Doing when it starts, does the work, then moves it to Done. Agents can only move a task to Done from Doing, so the work always shows as a step, even when it takes no time.
- **Needing more:** when the agent cannot go on without something from the user, it calls `ask_user` (REST `POST /api/tasks/{id}/ask-user` with `{ "question" }`). That posts its question as a comment, assigns the task to the user and puts it in Waiting, and sends the notify webhook `"<agent> needs your input: <title>"`. The user answers in the thread and assigns the task back to the agent.
- **Letting an agent decide who does it (the dispatcher):** mark one agent as the **dispatcher** in Settings. "Any agent" then means "let the dispatcher pick": only the dispatcher is woken, its inbox lists the task with reason `Dispatch`, and other agents cannot `claim_task` it. The dispatcher does no work on the task first: it reads `list_actors` (every agent's responsibilities) and assigns the task to the agent best placed to do it, which is woken and finds it in Waiting as `Assigned`. If it picks itself, it simply keeps the task and works it like any assigned task. If no agent fits (only the user can do it, or the request is unclear), it calls `ask_user` with the reason, which assigns the task to the user in Waiting and notifies them. A task assigned to the dispatcher by name is just its own work. With no dispatcher, "Any agent" works as before (every agent is woken and they race on `claim_task`). One agent can be both the triage agent and the dispatcher.
- **Asking without handing over (@mentions):** writing `@Name` in a comment (an agent's name or the user's, any case; the drawer has a button per agent) asks that actor for input without reassigning the task. A mentioned agent is woken (webhook `reason` `"Mentioned"`), its inbox lists the task as `Mentioned`, and it answers with a comment or a field; it may write a field even on a task another agent holds while the mention is open. The mention is answered once the mentioned actor comments, writes a field or calls `mark_handled`, and the drawer shows each mention as waiting or answered. An agent @mentioning the user sends the notify webhook `"<agent> mentioned you: <title>"`. To stop two agents waking each other forever, agent comments that @mention an agent are capped per task (`Board__MaxAgentMentionHops`, default 5); past it such a comment is refused with advice to drop the mention or use `ask_user`. A comment by the user resets the count.

Responsibilities: each agent has a short **Responsibilities** text (Settings) that the dispatcher reads. Adding an agent offers to ask the agent to fill it in itself, and **Ask it to describe itself** does the same later: DoiQ gives the agent a task "Describe your responsibilities" on the first board, which it picks up like any work (immediately by webhook, or on its first inbox sweep once it is connected) and answers with `set_my_responsibilities` (REST `PUT /api/me/responsibilities` with `{ "responsibilities" }`), replacing the text. The user can edit it afterwards.

What an agent may do to a task:

- **Its own tasks and the user's tasks:** edit, move to any lane (Done only from Doing), assign, add fields and subtasks. The user often works a task through an agent, so the agent keeps its status current; moving the user's task does not take it over, it stays assigned to the user.
- **Pool (`any`) and unassigned tasks:** edit and assign; move to Doing only while holding a live claim (the agent then becomes the assignee), to Done only once assigned to it.
- **Tasks another agent holds** (assigned to it, or live-claimed by it): comment only, plus fields while a comment there has @mentioned it and it has not answered yet.
- **Never:** delete tasks, or add, rename or delete boards. Writes accept a `requestKey` so a retried call is safe, and `upsert_field` matches the label case-insensitively so re-running a step updates one field.

### 5. What has and has not been verified

Verified automatically (`tests/DoiQ.Tests`, `HermesFlowTests` and `McpToolTests`, real server on a throwaway database with a local receiver standing in for Hermes) and once by hand against a standalone receiver: the user assigns a task, the debounced webhook arrives with a valid V2 signature and the documented payload, the agent works over `/mcp` with its key (`get_inbox`, `get_task`, `upsert_field`, `add_comment`, `move_task`, `assign_task`), the change shows on the board, the `doiq-notify` webhook fires, retries after a 500 and recovery, and a server restart with a row still queued.

**Not verified without a real Hermes:**

- That Hermes' webhook adapter verifies exactly this signature format. Run `hermes webhook test` against the `doiq` route and compare; if Hermes expects something else (different header name, timestamp tolerance, `sha256=` prefix) only `OutboxProcessor.Sign` and the header names in `src/DoiQ.Core/Outbox/OutboxProcessor.cs` need to change.
- The `hermes webhook subscribe` flags, the prompt template, `deliver_only` behaviour and the Hermes MCP config keys above, which are written from the plan, not from a running Hermes.
- That Hermes' MCP client accepts the stateless streamable-HTTP responses of `/mcp` (the .NET MCP client does).

## Backup and restore

The app backs itself up every night at 03:00 (the container's local time, UTC unless `TZ` is set) with SQLite's `VACUUM INTO`, which is safe while it runs in WAL mode. Each backup is integrity-checked and written to `./data/backups/doiq-<timestamp>.db`, the login keys are copied to `./data/backups/keys`, and the newest 14 are kept. Nothing needs configuring; to change it set `Backup__Hour` (0-23), `Backup__Keep`, `Backup__Directory`, or `Backup__Enabled=false` to switch it off.

Never copy `doiq.db` while the app is running: with WAL on, the data is split across `doiq.db`, `doiq.db-wal` and `doiq.db-shm`. With the app stopped (`docker compose stop doiq`), copying the whole `data` folder is a safe extra backup.

**Restore:**

```bash
docker compose down
mv data/doiq.db data/doiq.db.old
rm -f data/doiq.db-wal data/doiq.db-shm
cp data/backups/doiq-<timestamp>.db data/doiq.db
docker compose up -d
```

(On Linux, keep the owner at uid 1654: `sudo chown 1654:1654 data/doiq.db`.)

## Mobile and desktop app

`src/DoiQ.App` is a .NET MAUI app for Windows, Android and iOS. It shows the same UI as the browser, from `src/DoiQ.Client.UI`, in a native window, and talks to a DoiQ server you choose on first start. Plain `http://` on the LAN works.

- **Offline:** everything the app reads is cached on the device per server, in `DoiQ.App.Core`. If the server can't be reached, boards and tasks you opened before still show as they were last seen, with a banner saying so. Changes need a connection; nothing is queued.
- **What it leaves to the web UI:** board and agent administration, agent connection details and the password change. Settings in the app has the theme and the server address.
- **Notifications:** while the app runs in the background it notifies you about tasks that wait on you or mention you. These are local notifications driven by the live connection. There is no server push yet, so nothing arrives while the app is closed.

Building needs the MAUI workloads (`dotnet workload restore --project src/DoiQ.App/DoiQ.App.csproj`, from an admin terminal). CI has none, so the app is listed in `DoiQ.slnx` but not built with it: `dotnet build DoiQ.slnx` skips it, and Visual Studio builds it when you pick it. Build it on its own, all platforms or one at a time:

```bash
dotnet build src/DoiQ.App -p:AppPlatform=windows
dotnet build src/DoiQ.App -p:AppPlatform=android
dotnet build src/DoiQ.App -p:AppPlatform=ios
```

iOS needs a paired Mac.

## Local development

```bash
dotnet run --project src/DoiQ.Server
dotnet test
```

Without configuration the database is created at `<output folder>/data/doiq.db`. To use another file set `ConnectionStrings__Db`, e.g. `ConnectionStrings__Db="Data Source=./doiq.db"`. The image sets this to `Data Source=/data/doiq.db`.

Layout: `src/DoiQ.Shared` (DTOs), `src/DoiQ.Core` (logic, EF Core, migrations), `src/DoiQ.Server` (host), `src/DoiQ.Client.UI` (the UI: pages, components, services), `src/DoiQ.Client` (Blazor WASM host), `src/DoiQ.App` (MAUI app), `src/DoiQ.App.Core` (the app's offline cache and server settings), `tests/DoiQ.Tests` (server), `tests/DoiQ.Client.Tests` (bUnit), `tests/DoiQ.App.Tests` (app cache).
