# OutPost2 — Agent Instructions

@C:/repos/claude.interAgentComs/AGENT_INSTRUCTIONS.md

## This Agent's Identity

- **Name**: `outpost`
- **Workspace**: `C:/repos/outpost2`
- **Role**: OutPost2 — hosted, multi-user, multi-provider MCP server. Named Microsoft Entra
  tenant connections (1..N) plus pluggable non-Microsoft providers (YouTrack first).

## On Startup

```
register_agent("outpost", "C:/repos/outpost2", "OutPost2 - hosted multi-user MCP server with named Entra tenant connections (1..N) and pluggable providers (YouTrack).")
set_status("outpost", "online - ready for tasks")
check_inbox("outpost")
```

Call `heartbeat("outpost")` before starting a task, after a unit of work, and before
asking the user what to do next.

## Deployment-specific detail

Hostnames, tunnel ids, tracker URL and credential key names live in **`CLAUDE.local.md`**,
which is gitignored. Read it at the start of a session that needs them.

This file is committed, so nothing identifying an actual deployment belongs in it — the
split exists so this one can be public without redacting it later.

## Project Context

- **Tracker**: YouTrack project **OP2** (project id `0-9`). URL and credential key are in
  `CLAUDE.local.md`.
  The instance sits behind Cloudflare, which 1010-blocks the default `Python-urllib` UA —
  set a normal User-Agent on scripted access.
- **Baseline**: the `outpost` CLI — scope and shape only, no code reuse. Now retired.

## Local development

| Thing | Where |
|---|---|
| Local app | `http://localhost:5080` |
| Postgres | `localhost:5435` (5432/5433 taken by other projects) |
| Garage S3 | `http://localhost:3900`, buckets `op2-staging` / `op2-attachments` |
| Dev Vault | `http://localhost:8200`, `op2-transit/keys/op2-kek` |

All four are defined in `docker-compose.yml` and need no credentials beyond the dev
constants committed there. Those constants are bound to localhost containers and are not
reused anywhere real.

## Build

Driven by **Tamp** (`tamp-build/tamp`). The build is a C# program in `build/Build.cs`, not
YAML, so CI and a laptop run the identical thing.

```
tamp            # Compile
tamp Ci         # per-push gate: Compile, Test + coverage, MigrationCheck
tamp Nightly    # Ci plus secret scanning and the SBOM/SAST/SCA chain
tamp Smoke      # Image, Deploy, then prove the deployed app answers
```

`tamp` with the list flag enumerates targets; add the json format for the machine channel.
`tamp mcp` exposes the build itself over MCP, and the json reporter plus an events file
give an agent a typed stream rather than scraped console text.

Two targets are worth knowing about:

- **`MigrationCheck`** fails when the EF model has drifted from the migration snapshot —
  the "forgot to add a migration" bug, which otherwise only shows up on a fresh deploy.
  `dotnet ef` reaches the context through `DesignTimeDbContextFactory`, NOT by starting
  the web host, so startup validation can never block tooling.
- **`SecretScan`** runs trufflehog over the **whole git history**, verified-only, and fails
  the build on a live credential. This repo shipped a working GitHub PAT into a commit on
  2026-10-28 inside a redaction test's fixture; deleting it from HEAD would not have helped.
  CI must check out with `fetch-depth: 0` or the scan passes by only looking at one commit.

## Gotchas found the hard way

- `.NET 10` creates **`.slnx`**, not `.sln`.
- Git-Bash **mangles container paths** (`/garage` → `C:/Program Files/Git/garage`). Use the
  PowerShell tool for `docker exec`, or set `MSYS_NO_PATHCONV=1`.
- Git-Bash curl is **Schannel** and ignores `--cacert`, so it cannot reach a host behind a
  private CA. Use Python with `ssl.create_default_context(cafile=...)`.
- The **Garage image is distroless** — no shell, so init runs from the host via
  `docker compose exec garage /garage -c /etc/garage.toml ...`.
- Garage key IDs must be `GK` + exactly 24 hex chars; secrets exactly 64 hex chars.
- **MinIO images are no longer pullable** from Docker Hub or quay.io. Garage replaced it.
- `.env`, `.mcp.json` and `CLAUDE.local.md` are gitignored — `.env` holds the tunnel token.
- A **redaction test is the easiest place in the codebase to commit a real credential**:
  it needs a realistic string and it passes either way. Fixtures must be synthetic.
