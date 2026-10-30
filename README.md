# Butler

Tray app that runs Claude Code (`claude -p`) on triggers and shows what ran.

- Triggers: `%USERPROFILE%\.claude\skills\butler.json`, versioned with the skills.
- Schedule triggers run from Windows Task Scheduler (folder `\Butler\`), so they fire even when the app is closed.
- Mail triggers come from Outlook classic (COM, polled every 30 s from a checkpoint, so nothing is lost while Butler is closed).
- Runs (record + raw Claude output) and mail checkpoint in `%LOCALAPPDATA%\Butler`.
- See `CLAUDE.md` for the details.
