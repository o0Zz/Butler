# Butler

Tray app (Windows, macOS, Linux) that runs an agent CLI headless (Claude Code `claude -p` by default, or GitHub Copilot CLI) on triggers and shows what ran.
Single user, single machine: triggers live in the user's skills folder and call their skills, mailbox and folders.

## Rules
- Keep it small: tray and window in Avalonia code, UI in the single `dashboard.html`, no front-end build, no extra layers.
- No OS checks or `#if` outside `Platform/`: what differs by OS is a member of one of its interfaces.
- Every run is a separate Butler process (`run`, `answer`); the tray only polls and displays files. Exception: hotkey runs go through a Claude Code the tray keeps running (`QuickAgent`).
- Outlook is read-only: never send, move or flag mails.
- Manual tests: a scratch `BUTLER_DATA` whose `settings.json` points at a scratch triggers file.
- Tests use real things where possible: OS services (`tests/Butler.Tests/Windows`, `Unix`, compiled per OS; libsecret when a keyring runs), Outlook (skipped when not running), Azure DevOps (skipped unless `BUTLER_TEST_ADO_ORG` is the organization URL), folders, a fake CLI (`Fake`: .cmd on Windows, sh elsewhere).
- Nothing may rely on files next to the exe (single-file release).

## Settings
- `settings.json` (`Settings`), edited on the Settings page (tabs General, Agent, Triggers, To do, Backup) or by hand: `cli` (`claude`, `copilot`), `triggersFile`, `skillsFolder`, `adoOrganization`, `todo` (`none`, `todoist`, `txt`), `todoFile`, `todoHidden`, `startPage`, `language` (`auto`, `en`, `fr`), `backupFolder`, `models` (per CLI, the model of each level `fast`, `balanced`, `powerful`; empty: the CLI's default). Missing file: claude and `~/.claude/skills`. A new option gets a field on the Settings page.
- Saving refuses invalid values (`Settings.Problems`, by field, shown next to it; its tab is marked).
- Each process reads the settings when it starts. The tray watches `settings.json` and the triggers file and reloads.
- Start at login is the OS's own entry (`IStartAtLogin`: `Run` key, LaunchAgent, XDG autostart), on only when it starts this exe.
- Secrets (Todoist token) go in the OS's secret store (`ISecrets`, `Butler/Todoist`), never in `settings.json`; a new token is checked against Todoist first.
- `Backup`: with a `backupFolder` (not inside the skills folder), the poll thread checks hourly and writes `Butler-yyyy-MM-dd-HHmmss.zip` (skills folder without `.git` as `skills/`, the triggers file when elsewhere, `settings.json`) at most once a day and only when a file changed, was added or removed. Keeps the last 30, never touches other files. No secrets, runs or state. "Back up now" forces one; restoring is unzipping.

## Triggers
- The triggers file (default `~/.claude/skills/butler.json`, in the skills repo, not this one) is the source of truth; `Trigger.Load` creates it empty, with its folder. `on` is the kind; prompts use the kind's `${variables}` (`Trigger.PromptWith`; without any the event is appended; run by hand they stay unfilled with a note).
- `model` (prompts only): a level (`Settings.ModelFor` maps it per CLI) or a model name as is; none: the CLI's default. Recorded (`RunRecord.Model`); an answer continues with it.
- `prompt` (agent) or `command` (no agent): `IShell.CommandLine` (`cmd /d /v:on /s /c`, `sh -c`), `Runner.RunCommand`, run `Cli` `command`. Done on exit 0, last output line the summary, last 4000 chars the result, never waiting. The event goes in environment variables (`BUTLER_SUBJECT`...), never into the line (`${...}` refused): `!VAR!` with cmd, as `%VAR%` expands before parsing.
- Kinds: `src/Butler/Triggers/`, one `TriggerKind` per `on`, in `TriggerKind.All`: fields (unknown ones rejected; `Field`), variables, context, description, `Help` (for agents), `Form` (`text`, `list`, `folder`, `schedule`, `check`: the editor is built from it, no UI work), and for event kinds `Poll` + `Interval`. A new kind: the class and its tests.
- The tray polls every kind (no OS scheduler) on one background thread (Outlook's COM has its own STA thread in `Platform`). A poll completes or moves no checkpoint (`state/<kind>.json`); a new trigger, schedule, folder or filter starts from now.
- `schedule`: `daily HH:mm`, `fri HH:mm`, `mon-fri HH:mm`, `mon,wed,fri HH:mm`, `monthly:28 HH:mm` (no cron). Every 30 s; runs once when its latest time (`Schedule.Last`) is after its checkpoint, however many were missed. Only while the tray runs.
- `mail` (`from`): every folder of the default Outlook classic mailbox via COM every 30 s (Deleted, Outbox, Sent, Drafts, Junk skipped). `${from}` `${subject}` `${received}`.
- `ado` (`where`, WIQL): `az boards query` on `adoOrganization` every 2 min, through az's own `python.exe` (az.cmd breaks the WIQL quoting). `${id}` `${title}` `${state}` `${by}`.
- `file` (`folder`, optional `filter`): new files, not subfolders, once unchanged for 10 s. `${path}` `${name}`.
- `hotkey` (`keys` like `ctrl+shift+space`, optional `output` `paste`/`clipboard`, optional `myConfig`; Windows only, `IHotkeys`): runs the prompt on the selected text, `${text}`. The tray copies the selection (Ctrl+C once the keys are released), shows `WorkingLabel` by the pointer, then pastes the answer (Ctrl+V only if the same window is in front, else clipboard + notification) and restores the clipboard. Quick runs: no tools, MCP servers or protocol; without `myConfig`, Claude's system prompt replaced and user settings skipped, started from Butler's data folder. With Claude (`Cli.CanStayRunning`) one `QuickAgent` per CLI, model and config: started at the first press, `--input-format stream-json`, `/clear` after each prompt, killed after 60 s without an answer, closed when unused or at exit (~1 s instead of ~4). Otherwise (Copilot, Run now) `run <trigger> text=...` with `Runner.Quick`. Its `Poll` only reports keys taken by another app.
- The tray writes the `butler` skill (`ButlerSkill`, `<skillsFolder>/butler/SKILL.md`) at each reload from the kinds and settings, in English, only when changed and never over a file without its marker line, so any agent session can edit butler.json. It tells the agent to run `Butler.exe check` (`Program.Check`) after each change.
- Teams is not a kind: az's Graph token has no chat scopes.
- The editor saves by reading the file from disk, applying the change and validating all (`Trigger.Save` writes nothing when a trigger is wrong), keeping layout and line endings.

## Runs
- `Runner` starts the CLI from the user profile folder (this CLAUDE.md must not leak into runs). Claude: `claude -p --output-format stream-json --verbose --permission-mode auto --session-id <guid>`, protocol appended to the system prompt.
- Protocol: end with `BUTLER-ASK: <question>` (run Waiting) or `BUTLER-SUMMARY: <outcome>`. An answer resumes the same session.
- `Cli` holds what differs per CLI (arguments, JSON lines to text / tool / result events). A run records its `Cli`; answers and "Continue in terminal" use it. Copilot: protocol in the prompt, no result event (last message is the result, exit code the failure), no cost, `--allow-all-tools`; claude-in-chrome is claude only.
- A run records its process (`Pid`) and the CLI's (`CliPid`): `Stop` kills the CLI's tree; a Running run whose process is gone shows as Stopped.
- Data (`%LOCALAPPDATA%\Butler`, the OS's local app data elsewhere, `BUTLER_DATA` overrides): `settings.json`, `runs/<id>.json` + `.log` (raw stream), `state/`, `webview/`.

## Dashboard
- `dashboard.html` (embedded, plain JS, `marked.min.js` inlined) in Avalonia's `NativeWebView` (WebView2 / WebKit). C# pushes the whole state (`receive`); the page answers with `invokeCSharpAction` (`DashboardWindow.OnCommand`). Loaded once the engine exists (`AdapterCreated`); WebView2 needs the STA main thread and the manifest's supported OS list.
- Agent output can quote mails: raw HTML escaped, only http(s) links kept, the view never navigates (links open in the browser).
- Runs: header with waiting, running, today, failures over 7 days, cost this month (last 1000 runs, read at most once a minute). Triggers: cards with the last 10 runs and 30-day cost, editor drawer. Sidebar: "Watching" (each event kind's last poll and error, `TrayApp.Sources`), Settings at the bottom. Skills: folders with a `SKILL.md` and the triggers calling them (`/skill` at the start of the prompt). A card's trash deletes a trigger (as the editor's Delete) or moves a skill's folder to the OS's trash (`IShell.Trash`, only a folder `Skills.FolderOf` lists), after a confirmation naming the triggers still calling it; those are left as they are. Triggers and Skills have a search box (name, prompt, fields, description; case and accents ignored) that typing anywhere on the page, or Ctrl+F, fills.
- "Ask AI" opens the interactive CLI in a terminal (`IShell.Terminal`) in the skills folder, where the `butler` skill explains butler.json. No chat inside Butler.
- To do (`src/Butler/Todo/`, one `TodoBackend` per `todo`): Todoist API v1, or todo.txt (project = first `+tag`, id = the line). Add, insert, drag and drop (`Place` → `MoveTo` + `Reorder`; Todoist `/move` + Sync `item_reorder`), edit, complete with undo, delete, hide lists. Loaded off the UI thread, every minute and after each edit. Never overwrites a concurrent change: todo.txt refuses when the line is gone; Todoist sends a date only when changed (resending a recurring one resets it).
- Notifications (`INotifications`): Windows toasts with buttons (`ToastNotifications.OnToast` starts `Butler answer` or opens the dashboard); macOS (osascript), Linux (notify-send) without buttons.

## Platform
- `Platform/`: one implementation per OS of notifications, secrets, start at login, shell (open, reveal, trash, terminal, exe name, release suffix, `az`), mailbox, hotkeys. `Platform/Windows` builds for the Windows target, `Platform/Unix` for `net9.0`. `Program` builds it once (`Platform.Current`) and injects it.
- A second start reaches the running tray through a named pipe (`ShowSignal`).

## Languages
- English and French (`Lang`). The English text is the key: C# `T("...")`, page `tr('...')`, `data-tr`, `data-tr-title`; `src/Butler/Lang/fr.json` (embedded) maps it, `{0}` kept. Missing stays English. New user-visible text needs its French entry (`LangTests`). Not translated: parsing and tool errors, what goes to the agent.
- Set per process (`Lang.Use`: the tray at each reload, a run when it starts). The page gets `LANG` and `LOCALE` at load and reloads when the language changes.

## Commands
- `dotnet build`, `dotnet test` (repo root). `Butler.exe` (tray), `run <trigger> [var=value ...]`, `answer <runId> <text>`, `updated` (the tray after an update), `check` (settings and triggers file; pipe its output, it is a windowed exe).
- Version `X.Y.Z.<commit count>` (csproj `GitVersion`; `VersionBase` 0.0.0, CI passes the tag's), shown under the dashboard's title with its commit. The tray checks GitHub's latest release at start and daily (`Release.Newer`, not for 0.0.0) and links a newer one; Settings > General checks on demand. "Install and restart" downloads this OS's file (`IShell.ReleaseSuffix`), renames the running exe to `.old` (deleted at next start), moves the new one in, starts it with `updated` (waits for the old tray's mutex) and exits.
- CI (`.github/workflows/ci.yml`) builds and tests on Windows, Linux, macOS; a `v*` tag publishes `Butler-<version>.exe`, `-linux-x64`, `-osx-arm64` (single file, needs the .NET 9 runtime, Desktop Runtime on Windows). The Windows target builds only on Windows.
