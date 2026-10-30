# Butler

Windows tray app that runs Claude Code headless (`claude -p`) on triggers and shows what ran.
Single user, single machine: triggers live in the user's own skills folder and call their skills, mailbox and folders.

## How it works
- `%USERPROFILE%\.claude\skills\butler.json` is the source of truth. It lives in the skills git repo, next to the skills it calls (not in this repo). `on: "schedule"` (`daily HH:mm`, `weekly:mon HH:mm`, `monthly:28 HH:mm`) or `on: "mail"` (`from`: sender addresses). Mail prompts can use `${from}`, `${subject}`, `${received}` (filled by `Trigger.PromptWith`; without variables the mail is appended; run by hand they stay unfilled with a note).
- Schedule triggers: `TaskSync` reconciles the Task Scheduler folder `\Butler\` with the JSON (create / update / delete; a hash of schedule + exe path in the task description detects changes). Each task runs `Butler.exe run <name>`, which reads the prompt from the JSON at run time, so prompt edits need no sync. Tasks are "run only when user is logged on" (notifications and Chrome need the session) with "start when available" (catch-up).
- Mail triggers: the tray app polls every mail folder of the default Outlook classic mailbox through COM every 30 s (`OutlookInbox`; not only the Inbox, since rules move mails; Deleted, Outbox, Sent, Drafts and Junk are skipped), from a checkpoint in `state/mail.json` (`MailCheckpoint`), so mails received while Butler was closed are handled at the next start. First start begins at "now".
- Every run is a separate `Butler.exe` process (`run`, `answer`), never inside the tray process. The tray app only syncs, polls mail and displays files.
- `Runner` starts `claude -p --output-format stream-json --verbose --permission-mode auto --session-id <guid>` from the user profile folder (not this repo: this CLAUDE.md must not leak into triggered runs). It appends a protocol to the system prompt: end with `BUTLER-ASK: <question>` (run becomes Waiting) or `BUTLER-SUMMARY: <outcome>`. An answer resumes the same session with `--resume`. A run records its process (`Pid`) and claude's (`ClaudePid`): `Stop` kills claude's tree, and a Running run whose process is gone is marked Stopped when listed.
- Data in `%LOCALAPPDATA%\Butler`: `runs/<id>.json` (record) + `runs/<id>.log` (raw stream), `state/` (mail checkpoint), `webview/` (WebView2 profile).
- Dashboard: `dashboard.html` (embedded, plain JS, `marked.min.js` inlined for Markdown) in a WebView2. C# pushes the whole state as JSON; the page posts commands back (`DashboardForm.OnCommand`). Claude output can quote mail content: raw HTML is escaped, only http(s) links are kept, and the WebView never navigates away (links open in the browser).
- "Talk to Claude" (Triggers tab) opens an interactive `claude` in Windows Terminal in the skills folder, whose own CLAUDE.md explains butler.json: Claude edits the config, the watcher syncs and refreshes. No chat inside Butler.
- Skills tab: folders of `~/.claude/skills` with a `SKILL.md` (front matter `name`, `description`), and which triggers call them (`/skill` at the start of the prompt).
- Notifications: `Microsoft.Toolkit.Uwp.Notifications` toasts; buttons call back through `Program.OnToast`, which starts `Butler.exe answer ...` or opens the dashboard.

## Commands
- `dotnet build`, `dotnet test` (from the repo root).
- `Butler.exe` (tray), `Butler.exe run <trigger> [var=value ...]`, `Butler.exe answer <runId> <text>`, `Butler.exe sync`.
- `BUTLER_CONFIG` overrides the triggers file, `BUTLER_DATA` the data folder. Use a scratch `BUTLER_DATA` for manual tests.

## Rules
- Never touch Task Scheduler tasks outside `\Butler\`. Tests use a throwaway `ButlerTest-<guid>` folder and delete it.
- The tray app and `sync` always register tasks from the config they read: while testing, point `BUTLER_CONFIG` at a scratch file without schedule triggers, or accept that the real ones get (re)registered.
- Outlook access is read-only. Never send, move or flag mails.
- Tests run against real things where possible: real Task Scheduler, real Outlook (skipped when not running), and a fake `claude.cmd` for the runner.
- Keep it small: tray in WinForms code, UI in the single `dashboard.html`, no front-end build, no extra layers.
