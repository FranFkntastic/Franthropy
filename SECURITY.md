# Security Policy

## Reporting a Vulnerability

Use this repository's **Security** tab to submit a private vulnerability report.
Do not open a public issue for unsafe native interaction, path traversal,
sensitive-data exposure, authentication weaknesses, or other security defects.

Include the affected commit, operating conditions, a minimal reproduction, and
the consequence. Redact player identity, access tokens, local paths, screenshots,
logs, and plugin configuration from evidence.

## Supported Code

Security fixes target the current `local-dev` integration branch and are carried
to `main` when released. Older commits and locally modified builds may not
receive fixes.

## Safety Expectations

Patch-sensitive game mechanics must fail closed when their supported contract is
unknown. Shared APIs should expose explicit diagnostics rather than silently
guessing, and agent-facing mutation must remain declared, bounded, and
reviewable.
