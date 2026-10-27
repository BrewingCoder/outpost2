# OutPost2

Hosted, multi-user MCP server with **named** service connections — one person, several
unrelated Microsoft Entra tenants (and other providers), conversationally, from a phone.

Successor to the [`outpost`](https://github.com/signalclaude/outpost) CLI, which is the
baseline for scope and shape only. No code is reused.

## Why

Every open-source Microsoft/Outlook MCP server today is local stdio, single-operator, with
opaque account ids — so none of them can be reached from a phone at all. **Hosted**,
**multi-user** and **named** are the differentiators, not multi-tenancy alone.

## Status

Scaffold. Tracked in YouTrack project **OP2**.

## Stack

.NET 10 / ASP.NET Core · Postgres · Garage (S3) · HashiCorp Vault Transit ·
official MCP C# SDK v2 · Cloudflare tunnel

## Dev quickstart

```bash
cp .env.example .env      # fill in GitHub OAuth values
docker compose up -d
```

| Service | Local | Notes |
|---|---|---|
| web | http://localhost:5080 | also https://outpost.brewingcoder.com |
| postgres | localhost:5435 | |
| garage (S3) | http://localhost:3900 | buckets `op2-staging`, `op2-attachments` |
| vault (dev) | http://localhost:8200 | throwaway; `op2-transit/keys/op2-kek` |

Production points at the lab Vault at `https://vault.internal.example` instead — LAN only, never
via Cloudflare. Vault address, auth method and mount path are all config-driven, so no code
changes between dev and prod.
