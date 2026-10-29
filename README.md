# OutPost2

**Self-hosted, multi-user MCP server with *named* connections.** One person, several
unrelated Microsoft Entra tenants and other providers, reachable conversationally from a
phone.

Say *"check work email"* and it knows which mailbox. Say *"all"* and it checks every one.

---

## The problem it solves

Every open-source Microsoft/Outlook MCP server is local stdio, single-operator, and
addresses accounts by opaque id — so **none of them can be reached from a phone at all**,
and none of them can hold two tenants at once without you knowing which GUID is which.

That matters because the real use case is not sitting at a desk. It is a voice
conversation while driving, where you cannot read an id, cannot click a consent screen,
and cannot open Outlook to finish the job. Everything here follows from that: the web app
is configuration only, every tool answers in prose, and anything that cannot be said out
loud is a design bug.

## This is self-hosted. There is no service to sign up for.

Nobody sells seats on an OutPost2 instance, including us. You run your own, against your
own Entra app registration, holding your own users' tokens. `outpost.brewingcoder.com` is
one person's private deployment, not an invitation — it will not let you in.

The licence enforces this in both directions: you can run it commercially inside your own
organisation, and you may not run it as a service for other people. See
[Licence](#licence).

## What it does

**45 tools across three providers**, all resolved through the same named-connection model.

| Area | Tools |
|---|---|
| Connections | `list_connections` |
| Mail — read | `list_mail` `read_mail` `search_mail` `list_attachments` `read_attachment` |
| Mail — write | `send_mail` `send_mail_with_attachments` `reply_mail` `forward_mail` |
| Mail — manage | `mark_mail` `move_mail` `flag_mail` `categorise_mail` `list_categories` `report_junk` |
| Calendar — read | `list_events` `read_event` |
| Calendar — write | `create_event` `update_event` `cancel_event` `respond_to_invite` |
| Calendar — scheduling | `check_availability` `suggest_meeting_times` |
| People | `find_person` `list_contacts` `save_contact` `update_contact` |
| Issues (YouTrack) | `create_issue` `get_issue` `update_issue` `search_issues` `comment_on_issue` `list_projects` `create_project` |
| Code (GitHub) | `github_repositories` `github_repo_names` `github_issues` `github_pull_requests` `github_commits` `github_item` `github_triage` |
| Files and jobs | `list_files` `check_queue` + a web approval queue |
| Feedback | `report_friction` — the agent files its own bug reports mid-task |

Large attachments stream to Graph in 320 KiB-multiple chunks from object storage and never
buffer in memory, so sending is a resumable job rather than a tool call that times out.

Calendar times are the mailbox's own, not the server's: the mailbox timezone is read from
Graph and every reading and writing of a time goes through it, so "move it to two" means
two o'clock where you are and still does either side of a clock change.

`find_person` is what makes "email Justin" work: it merges the people you actually
correspond with, the company directory and your own address book, and says which of the
three each match came from, because that is usually how you tell two Justins apart.

## Getting started

You need Docker, and a **multi-tenant Entra app registration** with delegated permissions
only — never application permissions, which would grant access to every mailbox in a
tenant. That app registration is the one genuinely fiddly step; everything else is compose.

```bash
cp .env.example .env     # Entra + GitHub OAuth credentials, tunnel token, public URL
docker compose up -d
```

| Service | Local | Notes |
|---|---|---|
| web | http://localhost:5080 | |
| postgres | localhost:5435 | 5432/5433 are taken by other things on the author's host |
| garage (S3) | http://localhost:3900 | buckets `op2-staging`, `op2-attachments` |
| vault (dev) | http://localhost:8200 | throwaway; mirrors `op2-transit/op2-kek` |

Vault's address, auth method and mount path are all configuration, so pointing at a real
LAN Vault instead of the dev container needs no code change. In production it authenticates
with a Kubernetes service-account JWT and stores no secret at all.

Two things are on you and are **not** defaults:

- **Back up Vault.** Credentials are encrypted with per-row data keys wrapped by a Transit
  mount. Lose the Vault data and every stored credential becomes undecryptable.
- **Persist the ASP.NET data-protection keys**, or every redeploy signs all users out.

## Design decisions worth knowing

**A connection is not a mailbox.** It is an authorised link to one instance of one
provider, with a user-chosen name. Microsoft (OAuth), YouTrack and GitHub (PAT) are three
providers under one model; the same resolver serves all of them unchanged.

**Labels are unique per user, defaults are per provider.** My "work" is not your "work",
and "check email" and "my open tickets" resolve independently. Both are enforced by
database indexes, not application code.

**Granted and enabled are different states.** Granted costs an auth round trip and may
need an admin; enabled is a local toggle. Granted-but-disabled is legitimate, and the UI
never implies a grant was revoked when it wasn't.

**Writes never ride on a default.** An ambiguous send asks which connection. You cannot
unsend, and the recipient sees whichever identity was guessed.

**Mailbox changes are their own capability.** Filing, flagging and deleting are invisible
outside the mailbox, so gating them behind "send as me" would make *"tidy my inbox"*
require standing permission to write as the user — and gating them behind "read" would
mean switching off deletion also switches off reading.

**No opaque identifiers in the conversational path.** Graph message ids are unspeakable
base64, so listings carry short stable handles: `1. [a7kq] From — Subject`. This stopped
being cosmetic once filing existed, because a Graph *move reissues the message id* — a
positional reference breaks the moment anything is acted on, silently pointing at a
different message.

**Every tool answers in prose.** Relative times, not ISO timestamps. Every failure state
has a sentence someone can act on while driving.

## Build

Driven by [Tamp](https://github.com/tamp-build/tamp) — the build is a C# program in
`build/Build.cs`, not YAML, so CI and a laptop run the identical thing.

```bash
tamp          # compile
tamp Ci       # per-push gate: compile, test with coverage, migration-drift check
tamp Nightly  # Ci plus secret scanning and the full SBOM / SAST / SCA chain
tamp Smoke    # build the image, deploy it, prove it answers
```

The split is deliberate: every push runs only what needs no scanner binary, so CI stays
around two minutes, and everything that costs minutes of tool downloads runs nightly.

Two gates are worth calling out. **`MigrationCheck`** runs per-push despite being a
correctness gate rather than a test — it costs about a second and the bug it catches, a
model change with no migration, is invisible until a deploy against a fresh database.
**`SecretScan`** is nightly and reads the whole git history, verified-only, failing on a
live credential; that workflow checks out with `fetch-depth: 0`, because against a shallow
clone a history scan passes by examining one commit.

## Stack

.NET 10 / ASP.NET Core · Postgres · Garage (S3) · HashiCorp Vault Transit ·
official MCP C# SDK v2 · Tamp · Cloudflare tunnel

## Status

In daily use. **361 tests, none skipped** — the integration suites run against real
Postgres, real Garage, real Vault and a real YouTrack server, because the things worth
proving here are the ones a fake cannot.

| Area | State |
|---|---|
| GitHub sign-in, allowlisted by numeric id | done |
| Connection substrate (provider-agnostic, 1..N) | done |
| Vault Transit envelope encryption | done |
| Microsoft OAuth connect, reconnect, silent scope-up | done, verified against live tenants |
| Mail: read, search, send, reply, forward, file, flag, categorise | done |
| Attachments: staging, resumable large send, inbound extraction | done |
| YouTrack and GitHub providers | done |
| OAuth 2.1 server so an MCP client can connect | done |
| Voice mode reaching the connector | verified in the field |
| Calendar, contacts, free/busy | next |
| Teams, Gmail, Atlassian, Azure DevOps | planned |
| Cluster deployment | not started |

## Gotchas found the hard way

- `.NET 10` creates **`.slnx`**, not `.sln`.
- Git-Bash mangles container paths (`/garage` → `C:/Program Files/Git/garage`). Use
  PowerShell for `docker exec`, or set `MSYS_NO_PATHCONV=1`.
- Git-Bash curl is Schannel and ignores `--cacert`, so it cannot reach a host behind a
  private CA.
- The Garage image is distroless — no shell; drive its CLI from the host.
- Garage key ids are `GK` + exactly 24 hex chars; secrets exactly 64 hex.
- **AWS SDK v4** enables request checksums and `aws-chunked` by default, which Garage
  rejects as "Invalid payload signature". Set both to `WHEN_REQUIRED`.
- Cloudflare 1010-blocks unfamiliar user agents, including the default .NET and
  `Python-urllib` ones. Set a UA on anything scripted.
- YouTrack custom-field values are objects, arrays, strings *or* numbers. Parse
  defensively.
- MSAL's silent refresh fails the **whole** request if the cached token doesn't cover
  every requested scope — so adding one optional scope breaks every existing connection
  for every operation. Ask for the wide set, fall back to the required one.
- Graph rejects `$select` combined with `$expand` on `me/mailFolders` with a 400.
- A Graph **move reissues the message id**. Anything holding the old one is now pointing
  at nothing.

## Licence

[Elastic License 2.0](LICENSE). Use it, change it, run it — including commercially, inside
your own business, for your own people. **You may not run it as a service for third
parties.** That is the only form in which a hosted multi-user server can meaningfully be
taken and resold, and it is the one thing the licence stops.

Not an OSI-approved open-source licence, and deliberately not described as one. *Source
available, self-hosted, not resaleable* is the honest summary.

Security issues: see [SECURITY.md](SECURITY.md) — please report privately rather than
opening an issue.
