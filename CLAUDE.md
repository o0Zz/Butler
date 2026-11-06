# Butler

Tray app (Windows, macOS, Linux) that runs an agent CLI headless (Claude Code `claude -p` by default, or GitHub Copilot CLI) on triggers and shows what ran.
Single user, single machine: triggers live in the user's own skills folder and call their skills, mailbox and folders.

## Rules
- Keep it small: tray and window in Avalonia code, UI in the single `dashboard.html`, no front-end build, no extra layers.
- No OS checks or `#if` outside `Platform/`: what differs by OS is a member of one of its interfaces.
- Every run is a separate Butler process (`run`, `answer`), never inside the tray. The tray only polls triggers and displays files.
- Outlook access is read-only. Never send, move or flag mails.
- Manual tests: a scratch `BUTLER_DATA` whose `settings.json` points at a scratch triggers file (the tray starts the triggers of the config it reads).
- Tests run against real things where possible: OS services (Credential Manager, Run key, Keychain, LaunchAgent, autostart, libsecret when a keyring runs; `tests/Butler.Tests/Windows` and `Unix`, compiled per OS), Outlook (skipped when not running), Azure DevOps (skipped unless `BUTLER_TEST_ADO_ORG` is the organization URL), folders, and a fake CLI for the runner (`Fake`: .cmd on Windows, sh elsewhere).
- Nothing may rely on files next to the exe (released as a single file).

## Settings
- `%LOCALAPPDATA%\Butler\settings.json` (`Settings`), edited on the Settings page or by hand: `cli` (`claude`, `copilot`), `triggersFile`, `skillsFolder`, `adoOrganization`, `todo` (`none`, `todoist`, `txt`), `todoFile`, `todoHidden`, `startPage` (`runs` by default), `language` (`auto`, `en`, `fr`), `models` (per CLI, the model name of each level `light`, `mid`, `strong`; empty: the CLI's default). Missing file: claude and `~/.claude/skills`. New options go there, with a field on the Settings page.
- Saving refuses invalid values (`Settings.Problems`: unknown value, CLI not installed, missing file or folder), shown next to the fields.
- Each process reads the settings when it starts (a change applies to the next run). The tray watches `settings.json` and the triggers file and reloads.
- Start at login is not a setting: it is the OS's own entry (`IStartAtLogin`: `Run` registry key, LaunchAgent, XDG autostart), on only when it starts this exe.
- Secrets (the Todoist token) go in the OS's secret store (`ISecrets`, `Butler/Todoist`), never in `settings.json`. A new token is checked against Todoist first.

## Triggers
- The triggers file (default `~/.claude/skills/butler.json`, in the skills git repo, not this one) is the source of truth. `on` is the kind; event prompts use the kind's `${variables}` (`Trigger.PromptWith`; without variables the event is appended; run by hand they stay unfilled with a note).
- A prompt trigger can have a `model`: a level (`Settings.ModelFor` turns it into the CLI's model name, so triggers do not change with the CLI) or a model name passed as is; none: the CLI's default. The run records it (`RunRecord.Model`) and an answer continues with it. A command has no model.
- A trigger has a `prompt` (the agent) or a `command` (no agent): `IShell.CommandLine` (`cmd /d /v:on /s /c` on Windows, `sh -c` elsewhere), `Runner.RunCommand`, run `Cli` `command`. Done on exit 0, the last output line is the summary, the output (last 4000 chars) the result; never waiting. The event goes in environment variables (`BUTLER_SUBJECT`...), never pasted into the line (`${...}` refused): `!VAR!` with cmd, as `%VAR%` is expanded before the line is parsed.
- Kinds: `src/Butler/Triggers/`, one `TriggerKind` per `on` value, listed in `TriggerKind.All`. A kind declares its fields (unknown ones rejected; read with `Field`), variables, context, description, form, and for event kinds `Poll` + `Interval`. To add one: the class, its tests, its doc in the skills folder's CLAUDE.md. No UI work: the editor is built from `Form` (`text`, `list`, `folder`, `schedule`).
- The tray polls every kind (schedules included: no OS scheduler) on one STA thread. A poll completes or moves no checkpoint (`state/<kind>.json`); a new trigger, schedule, folder or filter starts from now.
- `schedule`: `daily HH:mm`, `fri HH:mm`, `mon-fri HH:mm`, `mon,wed,fri HH:mm`, `monthly:28 HH:mm` (no cron). Every 30 s; a trigger whose latest time (`Schedule.Last`) is after its checkpoint runs once, however many were missed. Runs only while the tray runs.
- `mail` (`from`): every folder of the default Outlook classic mailbox through COM every 30 s (rules move mails; Deleted, Outbox, Sent, Drafts, Junk skipped). `${from}` `${subject}` `${received}`.
- `ado` (`where`, WIQL): `az boards query` on `adoOrganization` every 2 min, through az's own `python.exe` (az.cmd's cmd.exe breaks the WIQL quoting). `${id}` `${title}` `${state}` `${by}`.
- `file` (`folder`, optional `filter`): new files, not subfolders, once unchanged for 10 s. `${path}` `${name}`.
- `hotkey` (`keys` like `ctrl+shift+space`, optional `output` `paste`/`clipboard`, optional `model`): a key combination in any app (`IHotkeys`, Windows only: `RegisterHotKey`) runs the prompt on the selected text, `${text}`. The tray copies the selection (Ctrl+C sent once the keys are released), starts `run <trigger> text=...`, waits for it, then pastes the answer in place (Ctrl+V, only if the same window is still in front, else the clipboard and a notification) and puts the clipboard's text back. The run is quick (`Runner.Quick`: no tools, no MCP servers, no protocol, its answer as is; ~4 s with sonnet) and only notifies when it fails. Its `Poll` starts nothing: it reports keys taken by another app.
- Teams is not a kind: az's Graph token has no chat scopes (needs an Entra app registration).
- Saving from the editor reads the file from disk, applies the change, validates everything (`Trigger.Save` writes nothing when a trigger is wrong) and keeps its layout and line endings.

## Runs
- `Runner` starts the CLI from the user profile folder (not this repo: this CLAUDE.md must not leak into runs). Claude: `claude -p --output-format stream-json --verbose --permission-mode auto --session-id <guid>`, the protocol appended to the system prompt.
- Protocol: end with `BUTLER-ASK: <question>` (run Waiting) or `BUTLER-SUMMARY: <outcome>`. An answer resumes the same session.
- `Cli` holds what differs per CLI (arguments, parsing its JSON lines into text / tool / result events). A run records its `Cli`; answers and "Continue in terminal" use that one. Copilot: no system prompt option (protocol in the prompt), no result event (the last message is the result, failure is the exit code), no cost, `--allow-all-tools` (no classifier like auto mode); claude-in-chrome is claude only.
- A run records its process (`Pid`) and the CLI's (`ClaudePid`): `Stop` kills the CLI's tree; a Running run whose process is gone is listed as Stopped.
- Data (`%LOCALAPPDATA%\Butler`, the OS's local app data elsewhere, `BUTLER_DATA` overrides): `settings.json`, `runs/<id>.json` + `.log` (raw stream), `state/`, `webview/`.

## Dashboard
- `dashboard.html` (embedded, plain JS, `marked.min.js` inlined) in Avalonia's `NativeWebView` (WebView2 / WebKit). C# pushes the whole state as JSON (`receive`); the page answers with `invokeCSharpAction` (`DashboardWindow.OnCommand`). Loaded once the engine exists (`AdapterCreated`); WebView2 needs the STA main thread and the manifest's supported OS list.
- Agent output can quote mail content: raw HTML is escaped, only http(s) links kept, the view never navigates away (links open in the browser).
- Runs: header with waiting, running, today, failures over 7 days, cost this month (last 1000 runs, read at most once a minute). Triggers: cards with the last 10 runs and 30-day cost, the editor drawer. Sidebar "Watching": each event kind's last poll and error (`TrayApp.Sources`); Settings at the bottom (occasional), where "Open folder" by the triggers file shows butler.json. Skills: folders with a `SKILL.md` and the triggers calling them (`/skill` at the start of the prompt).
- "Ask AI" opens the interactive CLI in a terminal (`IShell.Terminal`) in the skills folder, whose CLAUDE.md explains butler.json. No chat inside Butler.
- To do (`src/Butler/Todo/`, one `TodoBackend` per `todo` value): Todoist API v1, or todo.txt (project = first `+tag`, id = the line). Add, insert, drag and drop (`Place` → `MoveTo` + `Reorder`; Todoist `/move` + Sync `item_reorder`), edit, complete with undo, delete, hide lists. Loaded off the UI thread, every minute and after each edit. An edit never overwrites a concurrent change: todo.txt refuses when the line is gone; Todoist sends a date only when it changed (resending a recurring one resets it).
- Notifications (`INotifications`): Windows toasts with buttons (`ToastNotifications.OnToast` starts `Butler answer` or opens the dashboard); macOS (osascript) and Linux (notify-send) without buttons.

## Platform
- `Platform/` holds one implementation per OS of notifications, secrets, start at login, shell actions (open, reveal, terminal, exe name, release file suffix, `az`), the mailbox and hotkeys. `Platform/Windows` builds for the Windows target, `Platform/Unix` for `net9.0`. `Program` builds it once (`Platform.Current`) and injects it.
- A second start reaches the running tray through a named pipe (`ShowSignal`).

## Languages
- English and French (`Lang`). The English text is the key: C# `T("...")` (global `using static`), the page `tr('...')`, `data-tr`, `data-tr-title`; `src/Butler/Lang/fr.json` (embedded) maps it, `{0}` placeholders kept. Missing translations stay English. New user-visible text needs its French entry (`LangTests` checks). Not translated: file parsing and tool errors, what is sent to the agent.
- Set per process (`Lang.Use`: the tray at each reload, rebuilding its menu; a run when it starts). The page gets `LANG` and `LOCALE` when it loads and is reloaded when the language changes.

## Commands
- `dotnet build`, `dotnet test` (repo root). `Butler.exe` (tray), `Butler.exe run <trigger> [var=value ...]`, `Butler.exe answer <runId> <text>`, `Butler.exe updated` (the tray after an update).
- Version `X.Y.Z.<commit count>` (csproj `GitVersion`; `VersionBase` 0.0.0, CI passes the tag's), shown under the dashboard's title with its commit. The tray checks GitHub's latest release at start and daily (`Release.Newer`, not for 0.0.0 builds) and links a newer one there; Settings > Updates checks on demand. "Install and restart" (only when asked) downloads this OS's file (`IShell.ReleaseSuffix`), renames the running exe to `.old` (deleted at the next start), moves the new one in, starts it with `updated` (it waits for the old tray's mutex) and exits.
- CI (`.github/workflows/ci.yml`) builds and tests on Windows, Linux, macOS; a `v*` tag publishes `Butler-<version>.exe`, `-linux-x64`, `-osx-arm64` (single file, needs the .NET 9 runtime, Desktop Runtime on Windows). The Windows target builds only on Windows.
