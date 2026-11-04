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

**58 tools across four providers** — Microsoft 365, Gmail, YouTrack and GitHub — all
resolved through the same named-connection model.

| Area | Tools |
|---|---|
| Connections | `list_connections` |
| Mail — read | `list_mail` `read_mail` `search_mail` `list_attachments` `read_attachment` `save_attachment` |
| Mail — write | `send_mail` `send_mail_with_attachments` `reply_mail` `forward_mail` |
| Mail — manage | `mark_mail` `move_mail` `flag_mail` `categorise_mail` `list_categories` `report_junk` |
| Mail — folders | `list_folders` `create_folder` `rename_folder` `delete_folder` |
| Mail — bulk | `clear_mail` `recover_mail` |
| Calendar — read | `list_events` `read_event` |
| Calendar — write | `create_event` `update_event` `cancel_event` `respond_to_invite` |
| Calendar — scheduling | `check_availability` `suggest_meeting_times` |
| People | `find_person` `list_contacts` `save_contact` `update_contact` |
| Teams chats | `list_chats` `read_chat` `send_chat_message` |
| Issues (YouTrack) | `create_issue` `get_issue` `update_issue` `search_issues` `comment_on_issue` `list_projects` `create_project` |
| Code (GitHub) | `github_repositories` `github_repo_names` `github_issues` `github_pull_requests` `github_commits` `github_item` `github_triage` `github_files` `github_file` |
| Files and jobs | `stage_file` `list_files` `check_queue` + a web approval queue |
| Feedback | `report_friction` — the agent files its own bug reports mid-task |

**Gmail** serves every *Mail* row above (OP2-80). Calendar, People and Teams are Microsoft
Graph through and through, so a Gmail connection is never offered for them. Gmail is not
Outlook, and the tools say what they did in Gmail's own terms rather than pretending:

- **Labels, not folders.** Moving a message to a label adds the label and takes it out of
  the Inbox; archiving takes it out of the Inbox; categorising adds a label and leaves it
  where it was. Deleting a label deletes no mail — the messages stay in All Mail.
- **Trash and Spam**, not Deleted Items and Junk Email. Gmail empties Trash after 30 days,
  and `recover_mail` brings a sender back out of it.
- **Flags are stars**, which have no due date. **Importance** goes out as the headers
  Outlook and most apps show; Gmail itself does not display it.
- `report_junk` moves a message to Spam but **cannot block the sender** — that needs a
  Gmail settings permission Outpost does not ask for.
- Exact times are in UTC: Gmail has no mailbox time zone Outpost can read.

Large attachments stream to Graph in 320 KiB-multiple chunks from object storage and never
buffer in memory, so sending is a resumable job rather than a tool call that times out.

Calendar times are the mailbox's own, not the server's: the mailbox timezone is read from
Graph and every reading and writing of a time goes through it, so "move it to two" means
two o'clock where you are and still does either side of a clock change.

Some things happen when nobody is watching — a tenant administrator approving a
connection days after the user gave up, a long send finishing. Set a Telegram chat id on
the notifications page and you hear about those; set nothing and the settings page and
the next spoken answer still tell you, which is how it worked before and is why no
channel is required.

Teams is **chats only, and that is a permissions decision rather than an unfinished
one**. Every chat permission is user-consentable; reading a *channel* needs
`ChannelMessage.Read.All`, the one Teams permission requiring admin consent. Consent is
all or nothing, so adding it would mean an administrator had to approve mail and calendar
too. Channels stay out until that trade is worth making.

`find_person` is what makes "email Justin" work: it merges the people you actually
correspond with, the company directory and your own address book, and says which of the
three each match came from, because that is usually how you tell two Justins apart.

## Getting started

You need Docker, and a **multi-tenant Entra app registration** with delegated permissions
only — never application permissions, which would grant access to every mailbox in a
tenant. That app registration is the one genuinely fiddly step; everything else is compose.

One redirect URI is enough. Tenant admin approval comes back to the same
`/signin-microsoft` callback, told apart by Entra's own `admin_consent` parameter, so there
is nothing extra to register. Set `OutPost2__Entra__AdminConsentRedirectUri` only if you
want that trip to land somewhere else — and then register that URI too, or Entra refuses
it with AADSTS50011.

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

A **self-hosted** GitHub Enterprise or YouTrack behind a private certificate authority
needs that CA named: mount the PEM into the container and set
`OutPost2__TrustedRoots__CaCertPaths__0`. Nothing else changes — with no roots
configured, the platform's own certificate validation is used untouched, and a root that
is configured buys forgiveness for an untrusted *chain* only. A certificate issued for
the wrong hostname is still refused, which inside an organisation running its own CA is
the attack that matters.

Vault's address, auth method and mount path are all configuration, so pointing at a real
LAN Vault instead of the dev container needs no code change. In production it authenticates
with a Kubernetes service-account JWT and stores no secret at all.

**Gmail** is optional, and set up per deployment — see [Google setup](#google-setup).
Leave `GOOGLE_CLIENT_ID` / `GOOGLE_CLIENT_SECRET` blank and Gmail is simply not offered.

### Google setup

Every OutPost2 deployment uses **its own** Google OAuth client, in its own Google Cloud
project. There is no shared Outpost app to be approved: whoever runs an instance owns
the client, and Google's rules apply to that instance alone.

1. In Google Cloud Console, create a project, **enable the Gmail API**, and configure the
   OAuth consent screen.
2. Create an OAuth client of type **Web application** with
   `https://<your host>/signin-google` as an authorised redirect URI.
3. Put its id and secret in `GOOGLE_CLIENT_ID` / `GOOGLE_CLIENT_SECRET`.
4. Choose the consent screen's audience — this is the decision that matters:

| Setting | Who can connect | Sign-in lifetime | Google review |
|---|---|---|---|
| **External, published to production** — *recommended for personal use* | any Google account, up to 100 in total | lasts until revoked, or six months unused | none for personal use; users click through Google's "unverified app" warning |
| External, left in **Testing** | only accounts on the test-user list (up to 100) | **expires after seven days**, so every connection needs a weekly reconnect | none |
| **Internal** (project owned by a Google Workspace organisation) | only that organisation's accounts | lasts until revoked | none, and no warning |

Google exempts an app from verification when it is for personal use — fewer than 100
users, known to you — or internal to a Workspace organisation. A self-hosted instance
for yourself, your family or your team is exactly that. The **full** Google review —
domain and brand verification, a demo video, and an annual independent security
assessment (CASA) — is only for an app offered to the public, which the licence does not
allow anyway.

Two practical notes. Outpost's own sign-in is already limited to your allowlisted GitHub
users, so publishing the Google client to production does not open Gmail to anyone else;
Google's 100-account cap is an outer bound, not the gate. And the Cloud Console may still
flag the client as "needs verification" once it asks for Gmail scopes; for personal use
that can be left alone.

Two things are on you and are **not** defaults:

- **Back up Vault.** Credentials are encrypted with per-row data keys wrapped by a Transit
  mount. Lose the Vault data and every stored credential becomes undecryptable.
- **Persist the ASP.NET data-protection keys**, or every redeploy signs all users out.

## Design decisions worth knowing

**A connection is not a mailbox.** It is an authorised link to one instance of one
provider, with a user-chosen name. Microsoft and Google (OAuth), YouTrack and GitHub (PAT)
are four providers under one model; the same resolver serves all of them unchanged, and
mail is routed to the right provider's implementation by the connection alone.

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

The `Live*` probes run against real accounts and skip unless the dev Vault, the dev
database and a stored connection are there; secrets come from `~/.claude/credentials.json`.
`LiveGmailProbe` needs `outpost2_google_client_id` and `outpost2_google_client_secret` in
it and a Gmail connection made through the app (`OP2_PROBE_LABEL` picks one by name). It
reads, then stars, labels and moves a real message and puts everything back. It only
sends — to the account itself, then trashing its own mail — with `OP2_PROBE_SEND=1`:

```
OP2_PROBE_SEND=1 dotnet test --filter "FullyQualifiedName~LiveGmailProbe"
```

## Stack

.NET 10 / ASP.NET Core · Postgres · Garage (S3) · HashiCorp Vault Transit ·
official MCP C# SDK v2 · Tamp · Cloudflare tunnel

## Status

In daily use. **1,076 tests** — the integration suites run against real Postgres, real
Garage, real Vault and a real YouTrack server, because the things worth proving here are
the ones a fake cannot. The ones that skip are the live probes, which need real accounts:
the Microsoft ones a mailbox reconnected since the Teams scopes went in, `LiveGmailProbe`
a Gmail connection (and `OP2_PROBE_SEND=1` for its sending half).

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
| Calendar, contacts, free/busy | done |
| Teams chats: list, read, send | done |
| Teams channels | deferred — needs admin consent, see above |
| Telegram notifications (admin approval, job finished) | done; optional |
| Gmail: connect, read, search, send, organise | built against the Gmail API documentation; **live verification pending** — `LiveGmailProbe` (OP2-284) |
| Gmail sign-in for your own users | per-deployment Google client; no Google review needed for personal or internal use — see [Google setup](#google-setup) |
| Atlassian, Azure DevOps | planned |
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
- Gmail, per its documentation (not yet hit live — see `LiveGmailProbe`): a refresh token
  is issued only with `access_type=offline`, and on a *returning* account only with
  `prompt=consent` as well. An **attachment id changes on every fetch** of a message, so
  attachments are addressed by MIME part id. A `pageToken` is only valid with the query
  and labels it came from. `maxResults=0` is rejected outright.

## Licence

[Elastic License 2.0](LICENSE). Use it, change it, run it — including commercially, inside
your own business, for your own people. **You may not run it as a service for third
parties.** That is the only form in which a hosted multi-user server can meaningfully be
taken and resold, and it is the one thing the licence stops.

Not an OSI-approved open-source licence, and deliberately not described as one. *Source
available, self-hosted, not resaleable* is the honest summary.

Security issues: see [SECURITY.md](SECURITY.md) — please report privately rather than
opening an issue.
