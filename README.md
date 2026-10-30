<div align="center">

<img src="docs/butler.png" width="96" alt="Butler icon" />

# Butler

**Your coding agent, working while you don't.**
Butler runs Claude Code (or GitHub Copilot CLI) on its own: every morning, when a mail arrives, when a work item
changes, when a file lands in a folder. Then it tells you what it did, and asks when it needs you.

[![CI](https://github.com/o0Zz/butler/actions/workflows/ci.yml/badge.svg)](https://github.com/o0Zz/butler/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/o0Zz/butler)](https://github.com/o0Zz/butler/releases)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-informational)
![.NET](https://img.shields.io/badge/.NET-9-512BD4)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue)](LICENSE)

[Why Butler](#why-butler) · [What it does](#what-it-does) · [Triggers](#triggers) · [Get started](#get-started) · [How it works](#how-it-works)

<img src="docs/screenshots/runs.png" alt="Butler's dashboard: every run with its trigger, outcome, duration and cost, and the morning brief in Markdown" />

</div>

## Why Butler

Coding agents are great at the boring parts of the job: reading the mails that matter, filling the monthly
timesheet, turning a security alert into a ticket, summarizing the pull requests waiting for you. You already
wrote the skills for that. But they only run when **you** open a terminal and ask.

Butler is the missing piece: it **starts the agent for you**, at the right moment, without a window open, and
keeps an eye on what happened.

- **It runs on events, not on your memory.** A schedule, a mail from a given sender, an Azure DevOps change, a
  new file: each one starts a headless run of your agent with the prompt you chose.
- **Nothing is lost.** Schedules live in Windows Task Scheduler and catch up after sleep. Mails, work items and
  files are read from a checkpoint, so what arrived while your PC was off is handled when it wakes up.
- **It asks instead of guessing.** A run can stop with a question. You get a notification, answer it in one
  line, and the same session carries on.
- **You see everything.** A dashboard lists every run: what started it, what the agent did step by step, the
  outcome, the duration and the cost.
- **Your skills stay yours.** Triggers live in a plain `butler.json` next to your skills, in your own git repo.
  Butler adds no agent, no cloud and no account: it drives the CLI you already use, with your login.

## What it does

### A morning brief before you sit down
A schedule trigger at 08:30 runs `/morning-brief`: the important mails, today's meetings, the pull requests waiting
for your vote, and the mails wrongly caught as spam. The summary is waiting in the dashboard and in a notification.

### Mails handled as they arrive
A mail from `security@vendor.com` starts `/triage-mail` with the sender, subject and date. The agent opens a
ticket and prepares a reply as a draft. Butler reads every mail folder (rules that move mails don't hide them), and
never sends, moves or deletes a mail itself.

### Work that follows Azure DevOps
A WIQL condition like `[System.AssignedTo] = @Me AND [System.ChangedBy] <> @Me` starts a run for every work item
someone else changes for you, with its id, title and state.

### Files that process themselves
Drop a spec in a folder: once the file is fully written, a run starts with its path.

### The dashboard
- **Runs**: live activity of each run (text, tool calls), its outcome in Markdown, its cost; stop it, run it again,
  or continue the session in a terminal.
- **Waiting for you**: answer a run's question from the dashboard or straight from the notification.
- **Triggers**: one card per trigger, with its next run, its last 10 results and its cost over 30 days. Add, edit
  or delete triggers in a form; the schedule is picked from menus, and the prompt editor lists your skills and the
  variables of the trigger.
- **Watching**: whether each source is healthy (for example, Outlook is closed and mails are not being read).
- **Skills**: your skills and which triggers call them.
- **Ask AI**: opens your agent in the skills folder to write new skills and triggers in plain words.

<div align="center">
<img src="docs/screenshots/triggers.png" alt="Butler's triggers: a card per trigger with its schedule or source, its skill, its last runs and its cost" />
</div>

## Triggers

Triggers live in a JSON file (by default `~/.claude/skills/butler.json`), edited from the dashboard or by hand:

```json
[
  { "name": "morning-brief", "on": "schedule", "schedule": "daily 08:30", "prompt": "/morning-brief" },
  { "name": "timesheet", "on": "schedule", "schedule": "monthly:28 09:00", "prompt": "/timesheet" },
  { "name": "security-alerts", "on": "mail", "from": ["security@vendor.com"], "prompt": "/triage-mail ${from} ${subject}" },
  { "name": "my-items", "on": "ado", "where": "[System.AssignedTo] = @Me AND [System.ChangedBy] <> @Me", "prompt": "/review-item #${id} ${title}" },
  { "name": "specs", "on": "file", "folder": "%USERPROFILE%\\Downloads", "filter": "*.docx", "prompt": "/review-spec ${path}" }
]
```

| `on` | Fires | Variables |
|---|---|---|
| `schedule` | `daily HH:mm`, `weekly:mon HH:mm`, `monthly:28 HH:mm` (Windows Task Scheduler, even when Butler is closed) | none |
| `mail` | a mail from one of the `from` addresses, in any folder of Outlook classic (checked every 30 s) | `${from}` `${subject}` `${received}` |
| `ado` | a work item created or changed that matches the `where` WIQL condition (checked every 2 min) | `${id}` `${title}` `${state}` `${by}` |
| `file` | a new file in `folder`, optionally matching `filter` | `${path}` `${name}` |

A prompt starting with `/skill` calls that skill. Every trigger also has a **Run now** button.

## Get started

Download the zip of the latest [release](https://github.com/o0Zz/butler/releases), extract it anywhere and run
`Butler.exe`, or build it yourself (below).

Requirements:
- Windows 10 or 11, the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (the SDK to build)
  and the WebView2 runtime (built into Windows 11).
- An agent CLI, logged in: [Claude Code](https://docs.claude.com/en/docs/claude-code) (`claude`) or
  [GitHub Copilot CLI](https://github.com/features/copilot/cli) (`copilot`).
- For mail triggers: Outlook classic, running. For Azure DevOps triggers: the
  [Azure CLI](https://learn.microsoft.com/cli/azure/) with the `azure-devops` extension, logged in (`az login`).

```sh
git clone https://github.com/o0Zz/butler.git
cd butler
dotnet build
dotnet test
src/Butler/bin/Debug/net9.0-windows10.0.17763.0/Butler.exe
```

Butler starts in the tray. Open the dashboard, set your CLI, triggers file and skills folder on the **Settings**
page, then add your first trigger.

Versions are `X.Y.Z.N`, N being the number of commits: `0.0.0.N` for any build, `1.0.0.N` for the tag `v1.0.0`.
CI ([GitHub Actions](.github/workflows/ci.yml)) builds and tests every push and pull request; pushing a tag `vX.Y.Z`
also publishes the zip as a GitHub release:

```sh
git tag v1.0.0
git push origin v1.0.0
```

## How it works

- A tray app (.NET 9, WinForms) with a WebView2 dashboard.
- Each run is a separate `Butler.exe run <trigger>` process that starts the agent headless
  (`claude -p --output-format stream-json`), records its stream and its outcome, and shows a notification.
- Schedule triggers are tasks in the `\Butler\` folder of Task Scheduler, kept in sync with the JSON. Butler never
  touches other tasks.
- Mail, Azure DevOps and file triggers are polled by the tray from checkpoints stored in `%LOCALAPPDATA%\Butler`,
  together with the settings and the run history.
- A small protocol lets a run end with a question (`BUTLER-ASK`) or a one-line outcome (`BUTLER-SUMMARY`); an
  answer resumes the same agent session.

[`CLAUDE.md`](CLAUDE.md) describes the architecture and the design decisions in detail.

## License

[Apache License 2.0](LICENSE)
