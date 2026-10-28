# Security Policy

OutPost2 holds OAuth refresh tokens for other people's work mailboxes. A flaw here is a
flaw in someone's email, so please report privately rather than opening an issue.

## Reporting a vulnerability

Use **[GitHub private vulnerability reporting](https://github.com/BrewingCoder/outpost2/security/advisories/new)**
on this repository. It is private between you and the maintainer, and it works even if you
have never contributed here.

Please do not open a public issue, a pull request, or a discussion for a suspected
vulnerability. A public issue is a disclosure, and it is a disclosure made before anyone
running this has had a chance to patch.

Expect an acknowledgement within a few days. This is maintained by one person, so a fix
may take longer than that — you will get an honest estimate rather than silence.

## What is in scope

Anything that could expose a connection's credential or let one user act on another's
mailbox. Specifically:

- The token the server issues to an MCP client. It transitively unlocks every connection
  its owner holds and lives on someone else's infrastructure, so it is the most powerful
  object in the system. Per-connection scoping, expiry and revocation are all intended to
  work; a way around any of them is a finding.
- Stored provider credentials. They are envelope-encrypted with a per-row data key wrapped
  by Vault Transit. Anything that recovers a plaintext credential from the database alone
  is a finding.
- Connection isolation — one user reaching another user's connection, or a connection
  acting on a tenant it was not consented to.
- The OAuth and consent flows, including the reconnect path's identity guard.
- Anything that writes a credential, a mailbox address or message content into logs,
  errors or filed friction reports.

## What is not in scope

- The dev `docker-compose.yml` credentials. They are localhost-bound constants for a
  throwaway stack, published deliberately, and are not reused anywhere real.
- Findings that require an attacker to already hold the user's GitHub session or the
  server's Vault token.
- Missing hardening on a deployment you control. How you run it is yours; how the software
  behaves is ours.

## Running this yourself

If you deploy OutPost2, two things are on you and are not defaults:

- **Back up Vault.** Credentials are encrypted with keys wrapped by a Transit mount. Lose
  the Vault data and every stored credential becomes undecryptable — every user re-auths
  and anything encrypted is gone.
- **Persist the data-protection keys.** Otherwise every redeploy invalidates auth cookies
  and antiforgery tokens, which signs everyone out and can break an in-flight consent.
