# OutPost2

Hosted, multi-user MCP server with **named** service connections — one person, several
unrelated Microsoft Entra tenants (and other providers), conversationally, from a phone.

Successor to the [`outpost`](https://github.com/signalclaude/outpost) CLI, which is the
baseline for scope and shape only. No code is reused.

## Why

Every open-source Microsoft/Outlook MCP server today is local stdio, single-operator, with
opaque account ids — so **none of them can be reached from a phone at all**. Hosted,
multi-user and named are the differentiators, not multi-tenancy alone.

Say *"check work email"* and it knows which mailbox. Say *"all"* and it checks every one.

## Status

Working, deployed at `https://outpost.brewingcoder.com`. Tracked in YouTrack project **OP2**.

| Area | State |
|---|---|
| GitHub sign-in, allowlisted by numeric id | done |
| Connection substrate (provider-agnostic, 1..N) | done |
| Vault Transit envelope encryption | done |
| Microsoft OAuth connection flow | built, awaiting a live consent test |
| YouTrack provider + full tooling | done |
| Mail: read, search, send, HTML both ways | done |
| Attachments: staging, large send, inbound extraction | done |
| Job + approval queue | done |
| OAuth 2.1 server so Claude can connect | done |
| Cluster deployment | not started |

139 tests, none skipped. The integration suites run against real Postgres, real Garage, real
Vault and the real YouTrack lab server — the things a fake cannot prove.

## Design decisions worth knowing

**A connection is not a mailbox.** It is an authorised link to one instance of one provider,
with a user-chosen name. Microsoft (OAuth) and YouTrack (PAT) are two providers under one
model; the same resolver serves both unchanged.

**Labels are unique per user, defaults are per provider.** My "work" is not your "work", and
"check email" and "my open tickets" resolve independently. Both are enforced by database
indexes, not application code.

**Granted and enabled are different states.** Granted costs an auth round trip and may need
an admin; enabled is a local toggle. Granted-but-disabled is legitimate and the UI does not
pretend we revoked anything.

**Writes never ride on a default.** An ambiguous send asks which connection. You cannot
unsend, and the recipient sees whichever identity was guessed.

**No opaque identifiers in the conversational path.** Graph message ids are unspeakable
base64, and the config app deliberately never renders a mailbox — so listings are numbered
and "read the second one" works. Staged files are addressed by a name chosen at upload time,
when the user had a screen.

**Every tool answers in prose.** Relative times, not ISO timestamps. Every failure state has
a sentence someone can act on while driving.

## Stack

.NET 10 / ASP.NET Core · Postgres · Garage (S3) · HashiCorp Vault Transit ·
official MCP C# SDK v2 · Cloudflare tunnel

## Dev quickstart

```bash
cp .env.example .env      # fill in GitHub and Entra credentials
docker compose up -d
```

| Service | Local | Notes |
|---|---|---|
| web | http://localhost:5080 | also https://outpost.brewingcoder.com |
| postgres | localhost:5435 | 5432/5433 taken by other projects on this host |
| garage (S3) | http://localhost:3900 | buckets `op2-staging`, `op2-attachments` |
| vault (dev) | http://localhost:8200 | throwaway; mirrors `op2-transit/op2-kek` |

Vault address, auth method and mount path are configuration, so pointing at the lab Vault at
a private LAN Vault (never exposed publicly) needs no code change.

## Gotchas found the hard way

- `.NET 10` creates **`.slnx`**, not `.sln`.
- Git-Bash mangles container paths (`/garage` → `C:/Program Files/Git/garage`). Use
  PowerShell for `docker exec`, or set `MSYS_NO_PATHCONV=1`.
- Git-Bash curl is Schannel and ignores `--cacert`, so it cannot reach a host behind a private CA.
- The Garage image is distroless — no shell; drive its CLI from the host.
- Garage key ids are `GK` + exactly 24 hex chars; secrets exactly 64 hex.
- **AWS SDK v4** enables request checksums and `aws-chunked` by default, which Garage
  rejects as "Invalid payload signature". Set both to `WHEN_REQUIRED`.
- Cloudflare 1010-blocks unfamiliar user agents, including the default .NET and
  `Python-urllib` ones. Set a UA on anything scripted.
- YouTrack custom-field values are objects, arrays, strings *or* numbers. Parse defensively.

## License

[Elastic License 2.0](LICENSE). Use it, change it, run it — including commercially and
inside a business. The one thing you may not do is provide it to third parties as a
hosted or managed service, which is the only form in which this could be taken and sold.

Not an OSI-approved open-source licence, and deliberately so: the whole shape of this
project is a hosted service, so "source available, not resaleable" is the honest
description rather than a label it does not earn.

Security issues: see [SECURITY.md](SECURITY.md) — please report privately.
