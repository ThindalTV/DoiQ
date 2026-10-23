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

Go to **Settings** in the UI and add an agent (name, initials, whether it is the triage agent). The app creates an API key and shows it to you; copy it into the agent's configuration. Keys can be rotated from the same screen. Webhook URL and secret for each agent are also set there. Settings is the only place agents are managed; nothing about them is read from configuration.

**Forgot the password?** Stop the app and delete the stored hash; the next start shows first-run setup again (existing data is kept):

```bash
docker compose stop doiq
sqlite3 data/doiq.db "DELETE FROM Settings WHERE Key = 'password-hash';"
docker compose start doiq
```

## Boards

DoiQ can hold several boards, each with a name. A fresh database (and an upgraded one, whose existing tasks all move onto it) starts with one board called **Main**. Boards are fully separate: they do not interact, and a task never moves between boards (there is no endpoint, UI or MCP tool for it, and a move next to a task from another board is rejected).

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
| `doiq` | `{ "eventId", "boardId", "boardName", "taskId", "type", "actor", "title", "at" }` | The user creates, edits, comments on, adds a field to, or assigns a task that is the agent's to look at |
| `doiq-notify` | `{ "eventId", "boardId", "boardName", "taskId", "message" }` with `message` = `"<agent> assigned you: <title>"` | The agent creates a task for, or assigns a task to, the user |

Who gets a `doiq` webhook: the assignee if it is an agent; every active agent for a pool (`any`) task (they race on `claim_task`); only the triage agent for a task in New; agents a subtask is assigned to. Deactivated agents get nothing, and an agent's own changes never wake anyone. Changes to the same task and target within the debounce window (30 s, `Board__DebounceSeconds`) go out as one webhook carrying the latest event, so "create, enrich, assign, move" is one interruption. A webhook is a wake-up, not proof of work: Hermes should call `get_inbox`; a repeat wake finds nothing past `mark_handled` and does nothing.

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

The 18 tools: `list_boards`, `get_inbox`, `get_changes`, `search_tasks`, `get_task`, `create_task`, `update_task`, `move_task`, `assign_task`, `upsert_field`, `add_comment`, `mark_seen`, `mark_handled`, `add_subtask`, `update_subtask`, `claim_task`, `renew_claim`, `release_task`. Their descriptions are the agent's instructions (for example `move_task` says to move to Doing or Done only when the task is assigned to you, and to comment otherwise). Rule violations come back as tool errors with text the model can act on ("Comment on the user's tasks instead", "Task is already claimed by another agent"). Writes accept a `requestKey` so a retried call is safe, and `upsert_field` matches the label case-insensitively so re-running a step updates one field.

### 5. What has and has not been verified

Verified automatically (`tests/DoiQ.Tests`, `HermesFlowTests` and `McpToolTests`, real server on a throwaway database with a local receiver standing in for Hermes) and once by hand against a standalone receiver: the user assigns a task, the debounced webhook arrives with a valid V2 signature and the documented payload, the agent works over `/mcp` with its key (`get_inbox`, `get_task`, `upsert_field`, `add_comment`, `move_task`, `assign_task`), the change shows on the board, the `doiq-notify` webhook fires, retries after a 500 and recovery, and a server restart with a row still queued.

**Not verified without a real Hermes:**

- That Hermes' webhook adapter verifies exactly this signature format. Run `hermes webhook test` against the `doiq` route and compare; if Hermes expects something else (different header name, timestamp tolerance, `sha256=` prefix) only `OutboxProcessor.Sign` and the header names in `src/DoiQ.Core/Outbox/OutboxProcessor.cs` need to change.
- The `hermes webhook subscribe` flags, the prompt template, `deliver_only` behaviour and the Hermes MCP config keys above, which are written from the plan, not from a running Hermes.
- That Hermes' MCP client accepts the stateless streamable-HTTP responses of `/mcp` (the .NET MCP client does).

## Backup and restore

The `doiq-backup` service in `docker-compose.yml` runs `deploy/backup.sh` nightly at 03:00 (container time, UTC by default) using SQLite's online `.backup`, which is safe while the app is running in WAL mode. Backups are integrity-checked and written to `./data/backups/doiq-<timestamp>.db`; the newest 14 are kept. Tune with environment variables on that service: `BACKUP_HOUR` (default `3`), `BACKUP_KEEP` (default `14`), `TZ`.

Run one by hand:

```bash
docker compose run --rm --entrypoint /usr/local/bin/backup.sh doiq-backup
```

Never copy `doiq.db` while the app is running: with WAL on, the data is split across `doiq.db`, `doiq.db-wal` and `doiq.db-shm`.

**Restore:**

```bash
docker compose down
mv data/doiq.db data/doiq.db.old
rm -f data/doiq.db-wal data/doiq.db-shm
cp data/backups/doiq-<timestamp>.db data/doiq.db
docker compose up -d
```

(On Linux, keep the owner at uid 1654: `sudo chown 1654:1654 data/doiq.db`.)

## Local development

```bash
dotnet run --project src/DoiQ.Server
dotnet test
```

Without configuration the database is created at `<output folder>/data/doiq.db`. To use another file set `ConnectionStrings__Db`, e.g. `ConnectionStrings__Db="Data Source=./doiq.db"`. The image sets this to `Data Source=/data/doiq.db`.

Layout: `src/DoiQ.Shared` (DTOs), `src/DoiQ.Core` (logic, EF Core, migrations), `src/DoiQ.Server` (host), `src/DoiQ.Client` (Blazor WASM), `tests/DoiQ.Tests` (server), `tests/DoiQ.Client.Tests` (bUnit).
