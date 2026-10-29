# Desktop workflow

- Keep user-facing text in Vietnamese. The chat shows Codex-style thinking and tool cards (since 0.8.3);
  keep one live status line, and keep full detailed tool output in Terminal.
- Run tests in the background. Do not open visible UI test fixtures. Launch
  the actual Muse application only when the user asks to open it.
- After a desktop change, run relevant checks, including native offscreen
  UI checks for input/layout changes. Publish with `scripts/Publish-Desktop.ps1`
  (pass `-DotNet` when the SDK is not on PATH).
- The delivered application lives at `dist/Muse/Muse.exe`. Keep only the
  latest verified release; do not accumulate versioned distribution folders.
  The publish script tests and checks a staged build before removing old
  managed releases. Never delete old releases when validation fails.
- Preserve source changes and all personal data under LocalAppData,
  especially saved Muse login, browser state and chat histories. Do not
  force-close an interactive user application to replace a release; ask the
  user to close it if it is in use.
