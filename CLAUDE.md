# OutPost2 — Agent Instructions

@C:epos\claude.interAgentComs\AGENT_INSTRUCTIONS.md

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

## Project Context

- **Tracker**: YouTrack **OP2** (project id `0-9`) at `https://youtrack.example.com`.
  Epic OP2-1, subtasks OP2-2..OP2-16. Credential: `lab_youtrack_token`.
  Host is behind Cloudflare, which 1010-blocks the default `Python-urllib` UA — set a
  normal User-Agent on scripted access.
- **Use cases / decisions**: `C:/repos/outpost/docs/new-product-use-cases.md` (D1–D4).
- **Baseline**: the `outpost` CLI in `C:/repos/outpost` — scope and shape only, no code reuse.

## Live infrastructure

| Thing | Where |
|---|---|
| Public URL | `https://outpost.brewingcoder.com` (Cloudflare tunnel `outpost2-dev`, id `00000000-0000-0000-0000-000000000000`) |
| Local app | `http://localhost:5080` |
| Postgres | `localhost:5435` (5432/5433 taken by other projects) |
| Garage S3 | `http://localhost:3900`, buckets `op2-staging` / `op2-attachments`, key `GK` + 22×`0` + `d2` |
| Dev Vault | `http://localhost:8200`, root token `op2-dev-root`, `op2-transit/keys/op2-kek` |
| Lab Vault | `https://vault.internal.example` — LAN ONLY, never via Cloudflare (Scott's rule). AppRole creds in `~/.claude/credentials.json`: `vault_role_id`, `vault_secret_id`, CA at `vault_ca_cert` |

## Gotchas found the hard way

- `.NET 10` creates **`.slnx`**, not `.sln`.
- Git-Bash **mangles container paths** (`/garage` → `C:/Program Files/Git/garage`). Use the
  PowerShell tool for `docker exec`, or set `MSYS_NO_PATHCONV=1`.
- Git-Bash curl is **Schannel** and ignores `--cacert`, so it cannot reach `vault.internal.example`.
  Use Python with `ssl.create_default_context(cafile=...)`.
- The **Garage image is distroless** — no shell, so init runs from the host via
  `docker compose exec garage /garage -c /etc/garage.toml ...`.
- Garage key IDs must be `GK` + exactly 24 hex chars; secrets exactly 64 hex chars.
- **MinIO images are no longer pullable** from Docker Hub or quay.io. Garage replaced it.
- `.env` and `.mcp.json` are gitignored — `.env` holds the Cloudflare tunnel token.
