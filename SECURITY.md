# Security policy

## Responsible reporting

Please do **not** open a public GitHub issue containing API keys, cookies, session files, private user data, or exploit details. If GitHub private vulnerability reporting is enabled for this repository, use the repository's **Security** tab to report the issue privately. Otherwise, contact the maintainer using a private channel before disclosing details.

## Local credentials and runtime data

Muse stores local application data, browser login state, and conversation histories under `%LOCALAPPDATA%\Muse2Api.WinForms` on Windows. These directories may contain sensitive login information. Never commit or upload their contents.

Environment variables, secrets, browser state, private keys, SQLite databases, and generated release artifacts should not be committed. The repository's `.gitignore` includes common patterns, but **ignore rules do not protect content already tracked by Git or past commits**.

## If a secret was committed

1. Immediately revoke or rotate the affected token, password, or browser session.
2. Remove the secret from current tracked files.
3. Review Git history, forks, caches, and downstream clones; rewriting history requires coordination and does not invalidate leaked secrets.
4. Verify GitHub secret scanning alerts where available.

## Execution risk

The agent may run user-approved PowerShell/CMD commands with the permissions of the current Windows user. Such shell commands are not sandboxed to the selected workspace. Review commands before approval and do not run untrusted projects with elevated privileges.

## Current assurance

Source inspection and basic pattern checks do not constitute a full security audit. Build, runtime tests, dependency review, and historical secret scanning are still required.

## Windows credential encryption (upgrade branch)

Starting with Muse 0.8.6, gateway `accounts.json`, `api_key`,
and `browser-sessions/*.json` are encrypted with Windows DPAPI CurrentUser.
Valid legacy plaintext files are migrated after loading. Muse 0.8.5 cannot
read the encrypted format; use an isolated profile from
[UPGRADE_TESTING.md](UPGRADE_TESTING.md) before launching this branch.

The WebView2 profile and local conversation histories are not automatically
covered by these encrypted-file wrappers. The entire local profile should
be treated as sensitive. Non-Windows gateway storage is not DPAPI-protected.

Turbo may auto-approve normal file edits, but shell commands, process control
and sensitive file edits still require explicit review. It is not an OS sandbox.
